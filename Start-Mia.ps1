param(
    [switch]$Setup,
    [switch]$GatewayOnly,
    [switch]$ShowLaunchError,
    [switch]$PrepareWorkspaceOnly,
    [switch]$WorkCenter,
    [string]$OpenAiKeyFile,
    [string]$CodexPath,
    [string]$Model,
    [string]$SelfTestTaskFile
)

$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot '.mia-runtime'
$installHome = Join-Path $runtime 'hermes'
$exe = Join-Path $PSScriptRoot 'dist/MIA-3.2-win-x64/MIA.exe'
$service = $null
$launchLock = $null
$legacyLock = $null

function Invoke-Hermes {
    & $script:hermes -I -B $script:entry @args
    if ($LASTEXITCODE -ne 0) { throw "Hermes command failed (exit $LASTEXITCODE)." }
}

try {
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if (-not $documents -or [IO.Path]::GetPathRoot($documents) -ne 'C:\') {
        throw 'The Windows Documents folder must be on C: for MIA work.'
    }
    $workPath = Join-Path $documents 'MIA Workspace'
    # Reject redirected paths, including junctions above the workspace.
    foreach ($relative in @('', 'input', 'jobs', 'temp', 'state', 'state/hermes', 'state/mia', 'state/codex', 'logs', 'checks')) {
        $folder = if ($relative) { Join-Path $workPath $relative } else { $workPath }
        for ($parent = [IO.DirectoryInfo]::new($folder); $null -ne $parent; $parent = $parent.Parent) {
            if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "MIA work folders cannot use a redirected path: $($parent.FullName)"
            }
        }
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
    }
    if ($PrepareWorkspaceOnly) { Write-Output $workPath; return }
    $state = Join-Path $workPath 'state'
    $homePath = Join-Path $state 'hermes'
    $miaData = Join-Path $state 'mia'
    $launchLock = [IO.File]::Open((Join-Path $state 'launch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $env:MIA_TASK_TEMP = Join-Path $workPath 'temp'
    $env:TEMP = $env:MIA_TASK_TEMP
    $env:TMP = $env:MIA_TASK_TEMP
    $source = Join-Path $installHome 'hermes-agent'
    $entry = Join-Path $PSScriptRoot 'integration/hermes/mia_hermes.py'
    $hermes = Get-ChildItem -LiteralPath (Join-Path $installHome 'installs') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $record = Join-Path $_.FullName 'facts.json'
            if (Test-Path -LiteralPath $record -PathType Leaf) {
                $facts = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
                if ($facts.packages.venv.environment) { Join-Path $facts.packages.venv.environment 'Scripts/python.exe' }
            }
        } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $hermes) { throw 'Run Install-Hermes.ps1 first.' }
    $migrationMarker = Join-Path $state 'migrated-3.2'
    if (-not (Test-Path -LiteralPath $migrationMarker)) {
        $oldLockPath = Join-Path $runtime 'launch.lock'
        if (Test-Path -LiteralPath $oldLockPath) {
            try { $legacyLock = [IO.File]::Open($oldLockPath, 'Open', 'ReadWrite', 'None') }
            catch { throw 'Exit the previous version of MIA before moving its saved data.' }
        }
        # Move profile data, not the pinned installation and its package store.
        foreach ($name in @('config.yaml','.env','SOUL.md','auth.json','memories','sessions','plugins','skills','cron','kanban','state','state.db','state.db-wal','state.db-shm','response_store.db','response_store.db-wal','response_store.db-shm','kanban.db','kanban.db-wal','kanban.db-shm','runs_idempotency.db','runs_idempotency.db-wal','runs_idempotency.db-shm')) {
            $old = Join-Path $installHome $name
            if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath (Join-Path $homePath $name))) {
                Move-Item -LiteralPath $old -Destination $homePath
            }
        }
        foreach ($name in @('settings.json','hermes-session.txt','hermes-conversation.jsonl','conversation.jsonl','audio')) {
            $old = Join-Path $runtime "mia-data/$name"
            if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath (Join-Path $miaData $name))) {
                Move-Item -LiteralPath $old -Destination $miaData
            }
        }
        if ((Test-Path -LiteralPath (Join-Path $runtime 'openai.key')) -and -not (Test-Path -LiteralPath (Join-Path $state 'openai.key'))) {
            Move-Item -LiteralPath (Join-Path $runtime 'openai.key') -Destination $state
        }
        New-Item -ItemType File -Path $migrationMarker | Out-Null
    }
    $env:HERMES_HOME = $homePath
    $env:HERMES_RUNTIME_DIR = Join-Path $installHome 'tools'
    $env:MIA_HERMES_SOURCE = $source
    $env:MIA_HERMES_EXECUTABLE = $hermes
    $env:MIA_HERMES_ENTRY = $entry
    $env:MIA_HERMES_MEMORY_ENTRY = Join-Path $PSScriptRoot 'integration/hermes/mia_memory.py'
    $env:PYTHONDONTWRITEBYTECODE = '1'
    $env:UV_CACHE_DIR = Join-Path $workPath 'temp/uv-cache'
    $keyStore = Join-Path $state 'openai.key'
    if ($OpenAiKeyFile) {
        $keyText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $OpenAiKeyFile).Path)
        $key = [regex]::Match($keyText, '(?m)\bsk-[A-Za-z0-9_-]{12,}\b')
        if (-not $key.Success) { throw 'The specified key file does not contain an OpenAI API key.' }
        $env:OPENAI_API_KEY = $key.Value
        ConvertTo-SecureString -String $key.Value -AsPlainText -Force | ConvertFrom-SecureString |
            Set-Content -LiteralPath $keyStore -Encoding ASCII
        Remove-Variable keyText,key
        if (-not $Model) { $Model = 'gpt-6.1-terra' }
    }
    if (-not $env:OPENAI_API_KEY -and (Test-Path -LiteralPath $keyStore -PathType Leaf)) {
        $secret = (Get-Content -LiteralPath $keyStore -Raw).Trim() | ConvertTo-SecureString
        $env:OPENAI_API_KEY = [Net.NetworkCredential]::new('', $secret).Password
        Remove-Variable secret
    }

    $prefix = Join-Path $runtime 'bin/codex.exe'
    $codex = if ($CodexPath) { (Resolve-Path -LiteralPath $CodexPath).Path }
        elseif (Test-Path -LiteralPath $prefix -PathType Leaf) { $prefix }
        else { (Get-Command codex.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source }
    if (-not $codex) { throw 'Install the Codex CLI or pass -CodexPath to its executable.' }
    $env:MIA_CODEX_EXECUTABLE = $codex
    $env:MIA_CODEX_WORKSPACE = $workPath
    if (-not $env:MIA_CODEX_WINDOWS_SANDBOX) { $env:MIA_CODEX_WINDOWS_SANDBOX = 'mxc' }
    $codexHome = Join-Path $state 'codex'
    $previousCodexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    if ([IO.Path]::GetPathRoot($previousCodexHome) -eq 'C:\' -and
        -not (Test-Path -LiteralPath (Join-Path $codexHome 'auth.json')) -and
        (Test-Path -LiteralPath (Join-Path $previousCodexHome 'auth.json') -PathType Leaf)) {
        Copy-Item -LiteralPath (Join-Path $previousCodexHome 'auth.json') -Destination $codexHome
    }
    $env:CODEX_HOME = $codexHome
    New-Item -ItemType Directory -Force -Path (Join-Path $homePath 'plugins/mia-codex') | Out-Null
    $soul = Join-Path $homePath 'SOUL.md'
    if (-not (Test-Path -LiteralPath $soul) -or [IO.File]::ReadAllText($soul).StartsWith('You are Hermes Agent, built by Nous Research. Be direct:') -or [IO.File]::ReadAllText($soul).TrimStart().StartsWith(([IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'integration/hermes/SOUL.md'))[0]))) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'integration/hermes/SOUL.md') -Destination $soul
    }
    $plugin = Join-Path $homePath 'plugins/mia-codex'
    foreach ($name in @('plugin.yaml','__init__.py','bridge.py','extensions.py','history.py')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "integration/hermes/mia-codex/$name") -Destination $plugin
    }

    if ($Setup) { Invoke-Hermes setup }
    if ($env:OPENAI_API_KEY) {
        if (-not $Model -and -not (Test-Path -LiteralPath $keyStore -PathType Leaf)) { $Model = 'gpt-6.1-terra' }
        Invoke-Hermes config set model.provider openai-api
        Invoke-Hermes config set model.base_url https://api.openai.com/v1
    }
    if ($Model) { Invoke-Hermes config set model.default $Model }
    Invoke-Hermes plugins enable mia-codex
    Invoke-Hermes config set memory.provider none

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $env:API_SERVER_ENABLED = 'true'
    $env:API_SERVER_HOST = '127.0.0.1'
    $env:API_SERVER_PORT = [string]$port
    $env:API_SERVER_KEY = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
    $env:MIA_HERMES_URL = "http://127.0.0.1:$port"
    $env:MIA_HERMES_KEY = $env:API_SERVER_KEY
    $env:MIA_START_LAUNCHER = $PSCommandPath
    $env:MIA_DATA_DIR = $miaData
    if ($SelfTestTaskFile) { $env:MIA_DATA_DIR = Join-Path $workPath ('checks/' + [guid]::NewGuid().ToString('N')) }
    # Keep the API tool selection local to this profile and avoid starting other adapters.
    Invoke-Hermes config set platform_toolsets.api_server '[web, vision, todo, clarify, memory, session_search, mia_codex]'
    $arguments = @('-I','-B',('"{0}"' -f $entry),'gateway','run')
    $service = Start-Process -FilePath $hermes -ArgumentList $arguments -WorkingDirectory $workPath -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $workPath 'logs/hermes-gateway.log') -RedirectStandardError (Join-Path $workPath 'logs/hermes-gateway-error.log')
    $headers = @{ Authorization = "Bearer $env:API_SERVER_KEY" }
    $ready = $false
    for ($attempt = 0; $attempt -lt 120 -and -not $service.HasExited; $attempt++) {
        try {
            $null = Invoke-RestMethod -Uri "$env:MIA_HERMES_URL/v1/toolsets" -Headers $headers -TimeoutSec 2
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 400 }
    }
    if (-not $ready) { throw "Hermes did not start. Check $workPath\logs\hermes-gateway-error.log." }

    if ($GatewayOnly) {
        Write-Host "Hermes ready on $env:MIA_HERMES_URL. This process owns the Gateway."
        $service.WaitForExit()
    } else {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Run build.ps1 first.' }
        $miaArgs = @()
        if ($SelfTestTaskFile) { $miaArgs = @('--hermes-self-test-file', ('"{0}"' -f (Resolve-Path -LiteralPath $SelfTestTaskFile).Path)) }
        elseif ($WorkCenter) { $miaArgs = @('--work-center') }
        $mia = if ($miaArgs.Count) { Start-Process -FilePath $exe -ArgumentList $miaArgs -WorkingDirectory $workPath -PassThru }
            else { Start-Process -FilePath $exe -WorkingDirectory $workPath -PassThru }
        $mia.WaitForExit()
        if ($SelfTestTaskFile) { Write-Host "MIA check result: $env:MIA_DATA_DIR\hermes-ui-check.json" }
        if ($mia.ExitCode -ne 0) { throw "MIA exited with code $($mia.ExitCode)." }
    }
} catch {
    if ($ShowLaunchError) {
        Add-Type -AssemblyName PresentationFramework
        $null = [Windows.MessageBox]::Show($_.Exception.Message, 'MIA 3.2')
    }
    throw
} finally {
    if ($service -and -not $service.HasExited) {
        # Stop only the process tree started by this launcher.
        & "$env:SystemRoot\System32\taskkill.exe" /pid $service.Id /t /f 2>$null | Out-Null
    }
    if ($launchLock) { $launchLock.Dispose() }
    if ($legacyLock) { $legacyLock.Dispose() }
}
