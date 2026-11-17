param(
    [switch]$Setup,
    [switch]$GatewayOnly,
    [switch]$ShowLaunchError,
    [switch]$PrepareWorkspaceOnly,
    [switch]$WorkCenter,
    [switch]$BrowserLogin,
    [switch]$EnableIsolatedCode,
    [string]$DiscordUserId,
    [string]$DiscordMiaUserId = 'local-jason',
    [string]$DiscordDisplayName,
    [string]$DiscordTokenFile,
    [string[]]$McpCommand,
    [string]$WebhookRules,
    [ValidateRange(1024,65535)][int]$WebhookPort = 8644,
    [string]$AppPath,
    [string]$OpenAiKeyFile,
    [string]$CodexPath,
    [string]$Model = 'gpt-6.1-sol',
    [string]$SelfTestTaskFile
)

$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot '.mia-runtime'
$installHome = Join-Path $runtime 'hermes'
$exe = Join-Path $PSScriptRoot 'dist/MIA-3.2-win-x64/MIA.exe'
$service = $null
$launchLock = $null
$legacyLock = $null
$loading = $null
$progressPath = $null

function Set-LaunchProgress([int]$Percent, [string]$Message) {
    if ($script:progressPath) {
        @{percent=$Percent; message=$Message} | ConvertTo-Json -Compress |
            Set-Content -LiteralPath $script:progressPath -Encoding UTF8
    }
}

function Invoke-Hermes {
    & $script:hermes -I -B $script:entry @args
    if ($LASTEXITCODE -ne 0) { throw "Hermes command failed (exit $LASTEXITCODE)." }
}

try {
    # A PowerShell 7 parent can leave incompatible modules earlier in PSModulePath.
    # Use this host's built-in DPAPI cmdlets, including when launched from VBS.
    Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Security/Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if (-not $documents -or [IO.Path]::GetPathRoot($documents) -ne 'C:\') {
        throw 'The Windows Documents folder must be on C: for MIA work.'
    }
    $workPath = Join-Path $documents 'MIA Workspace'
    # Reject redirected paths, including junctions above the workspace.
    foreach ($relative in @('', 'input', 'jobs', 'templates', 'downloads', 'temp', 'state', 'state/hermes', 'state/mia', 'state/codex', 'logs', 'checks')) {
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
    if (-not $GatewayOnly -and -not $Setup -and -not $McpCommand -and -not $SelfTestTaskFile -and -not $DiscordUserId -and -not $DiscordTokenFile) {
        $progressExe = if ($AppPath) { $AppPath } else { $exe }
        if (Test-Path -LiteralPath $progressExe -PathType Leaf) {
            $progressPath = Join-Path $workPath ('temp/launch-' + [guid]::NewGuid().ToString('N') + '.json')
            $env:MIA_LAUNCH_PROGRESS = $progressPath
            Set-LaunchProgress 8 '整理工作空間…'
            $loading = Start-Process -FilePath $progressExe -ArgumentList @('--loading', ('"{0}"' -f $progressPath), "$PID") -PassThru
        }
    }
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
    Set-LaunchProgress 22 '載入米亞設定與工具…'
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
    $env:MIA_HERMES_KANBAN_ENTRY = Join-Path $PSScriptRoot 'integration/hermes/mia_kanban.py'
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
        if (-not $Model) { $Model = 'gpt-6.1-sol' }
    }
    if (-not $env:OPENAI_API_KEY -and (Test-Path -LiteralPath $keyStore -PathType Leaf)) {
        $secret = (Get-Content -LiteralPath $keyStore -Raw).Trim() | ConvertTo-SecureString
        $env:OPENAI_API_KEY = [Net.NetworkCredential]::new('', $secret).Password
        Remove-Variable secret
    }
    $localKeyFile = Join-Path $PSScriptRoot 'api_key'
    if (-not $env:OPENAI_API_KEY -and (Test-Path -LiteralPath $localKeyFile -PathType Leaf)) {
        $key = [regex]::Match([IO.File]::ReadAllText($localKeyFile), '(?m)\bsk-[A-Za-z0-9_-]{12,}\b')
        if ($key.Success) {
            $env:OPENAI_API_KEY = $key.Value
            if (-not $Model) { $Model = 'gpt-6.1-sol' }
        }
        Remove-Variable key
    }

    $prefix = Join-Path $runtime 'bin/codex.exe'
    $codex = if ($CodexPath) { (Resolve-Path -LiteralPath $CodexPath).Path }
        elseif (Test-Path -LiteralPath $prefix -PathType Leaf) { $prefix }
        else { (Get-Command codex.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source }
    if (-not $codex) { throw 'Install the Codex CLI or pass -CodexPath to its executable.' }
    $env:MIA_CODEX_EXECUTABLE = $codex
    $env:MIA_CODEX_WORKSPACE = $workPath
    $env:MIA_CODEX_MODEL = $Model
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
    foreach ($name in @('plugin.yaml','__init__.py','bridge.py','extensions.py','history.py','browser_tools.py','scheduled_tasks.py','webhook_events.py','isolated_code.py','work_cards.py','templates.py','desktop_tools.py','voice_export.py','permissions.py','powerpoint.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "integration/hermes/mia-codex/$name") -Destination $plugin
    }
    $calendarSkill = Join-Path $homePath 'skills/mia-calendar'
    New-Item -ItemType Directory -Force -Path $calendarSkill | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $calendarSkill 'SKILL.md'))) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'integration/hermes/skills/mia-calendar/SKILL.md') -Destination $calendarSkill
    }

    Set-LaunchProgress 38 '連接行事曆與工作服務…'
    Invoke-Hermes mia-configure-calendar
    if ($McpCommand) {
        if ($McpCommand[0] -in @('enable','disable')) {
            if ($McpCommand.Count -ne 2) { throw 'Supply the MCP server name.' }
            Invoke-Hermes mia-mcp-toggle @McpCommand
            return
        }
        if ($McpCommand[0] -notin @('add','list','test','login','reauth','configure','config','remove','rm','catalog')) {
            throw 'Use -McpCommand with add, list, test, login, reauth, configure, remove or catalog.'
        }
        Invoke-Hermes mcp @McpCommand
        return
    }

    if ($Setup) { Invoke-Hermes setup }
    if ($BrowserLogin) {
        $browserProfile = Join-Path $homePath 'mia-browser-profile'
        Invoke-Hermes config set browser.user_data_dir $browserProfile
        Invoke-Hermes config set browser.headed true
    }
    if ($env:OPENAI_API_KEY) {
        if (-not $Model -and -not (Test-Path -LiteralPath $keyStore -PathType Leaf)) { $Model = 'gpt-6.1-sol' }
        Invoke-Hermes config set model.provider openai-api
        Invoke-Hermes config set model.base_url https://api.openai.com/v1
    }
    if ($Model) { Invoke-Hermes config set model.default $Model }
    Invoke-Hermes plugins enable mia-codex
    Invoke-Hermes config set memory.provider none
    Invoke-Hermes config set kanban.dispatch_in_gateway false

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
    $env:MIA_VOICE_EXPORT_EXE = if ($AppPath) { (Resolve-Path -LiteralPath $AppPath).Path } else { $exe }
    $env:MIA_CRON_DELIVERY_ENABLED = 'true'
    $env:MIA_CRON_HOME_CHANNEL = 'desktop'
    $env:MIA_WEBHOOK_RULES = if ($WebhookRules) { (Resolve-Path -LiteralPath $WebhookRules).Path } else { $null }
    $env:MIA_WEBHOOK_PORT = [string]$WebhookPort
    if ($SelfTestTaskFile) { $env:MIA_DATA_DIR = Join-Path $workPath ('checks/' + [guid]::NewGuid().ToString('N')) }
    # Keep the API tool selection local to this profile and avoid starting other adapters.
    $toolsets = @('web','vision','todo','clarify','memory','session_search','skills','mia_codex')
    if ($EnableIsolatedCode) {
        if (-not (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)) {
            throw 'Install and start Docker Desktop before using -EnableIsolatedCode.'
        }
        $containerOs = & docker info --format '{{.OSType}}'
        if ($LASTEXITCODE -ne 0) { throw 'Docker is not ready.' }
        if ($containerOs -ne 'linux') { throw 'Switch Docker Desktop to Linux containers.' }
        $env:MIA_ISOLATED_CODE = '1'
        Invoke-Hermes config set terminal.backend docker
        Invoke-Hermes config set terminal.docker_network false
        Invoke-Hermes config set terminal.docker_volumes '[]'
        Invoke-Hermes config set terminal.docker_extra_args '[]'
        Invoke-Hermes config set terminal.docker_env '{}'
        Invoke-Hermes config set terminal.docker_mount_cwd_to_workspace false
        Invoke-Hermes config set terminal.docker_forward_env '[]'
        Invoke-Hermes config set terminal.container_persistent false
        Invoke-Hermes config set terminal.docker_persist_across_processes false
        Invoke-Hermes config set terminal.docker_shared_container_key '""'
        Invoke-Hermes config set proxy.enabled false
        Invoke-Hermes config set code_execution.mode strict
        Invoke-Hermes config set code_execution.timeout 60
        Invoke-Hermes config set code_execution.max_tool_calls 12
    } else {
        $env:MIA_ISOLATED_CODE = $null
    }
    Invoke-Hermes config set platform_toolsets.api_server ('[' + ($toolsets -join ', ') + ']')
    $identityPath = Join-Path $miaData 'users.json'
    $env:MIA_HERMES_PROFILE_ROOT = $homePath
    $env:MIA_HERMES_USER_REGISTRY = if (Test-Path -LiteralPath $identityPath) { $identityPath }
        else { Join-Path $PSScriptRoot 'src/Mia/users.json' }
    $discordKeyPath = Join-Path $state 'discord.key'
    $discordEntry = Join-Path $PSScriptRoot 'integration/hermes/mia_discord.py'
    if ($DiscordUserId) {
        if (-not (Test-Path -LiteralPath $identityPath)) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/Mia/users.json') -Destination $identityPath
        }
        $bindingArgs = @('-I','-B',$discordEntry,'--registry',$identityPath,'--bind',$DiscordUserId,'--user',$DiscordMiaUserId)
        if ($DiscordDisplayName) { $bindingArgs += @('--name',$DiscordDisplayName) }
        & $hermes @bindingArgs
        if ($LASTEXITCODE -ne 0) { throw 'Discord identity binding failed.' }
        $env:MIA_HERMES_USER_REGISTRY = $identityPath
    }
    if ($DiscordTokenFile) {
        $discordToken = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $DiscordTokenFile).Path).Trim()
        if ($discordToken -match '(?im)^\s*\[discord\]\s*$') {
            $discordSection = [regex]::Match($discordToken, '(?ims)^\s*\[discord\]\s*\r?\n(.*?)(?=^\s*\[|\z)').Groups[1].Value
            $discordToken = [regex]::Match($discordSection, '(?im)^\s*token\s*=\s*(\S+)\s*$').Groups[1].Value
            Remove-Variable discordSection
        }
        if (-not $discordToken -or $discordToken -match '\s') { throw 'The file must contain only the Discord Bot Token.' }
        ConvertTo-SecureString -String $discordToken -AsPlainText -Force | ConvertFrom-SecureString |
            Set-Content -LiteralPath $discordKeyPath -Encoding ASCII
        Remove-Variable discordToken
    }
    if (Test-Path -LiteralPath $discordKeyPath) {
        $discordSecret = (Get-Content -LiteralPath $discordKeyPath -Raw).Trim() | ConvertTo-SecureString
        $env:DISCORD_BOT_TOKEN = [Net.NetworkCredential]::new('', $discordSecret).Password
        Remove-Variable discordSecret
        & $hermes -I -B -c 'import discord, youtube_transcript_api, reportlab, docx'
        if ($LASTEXITCODE -ne 0) { throw 'Discord dependencies missing. Run Install-Hermes.ps1 to install them.' }
        $roleExe = if ($AppPath) { $AppPath } else { $exe }
        $rolePath = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($roleExe))) 'role.json'
        & $hermes -I -B $discordEntry --registry $identityPath --role $rolePath
        if ($LASTEXITCODE -ne 0) { throw 'Discord profile preparation failed.' }
    }
    if ($DiscordUserId -or $DiscordTokenFile) { Write-Output 'Discord settings saved. Start MIA normally to connect.'; return }
    Set-LaunchProgress 64 '啟動 Hermes，等待連線…'
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
    Set-LaunchProgress 88 '載入人物與對話介面…'
    if ($WebhookRules) {
        $webhookReady = $false
        for ($attempt = 0; $attempt -lt 20 -and -not $service.HasExited; $attempt++) {
            try {
                $health = Invoke-RestMethod -Uri "http://127.0.0.1:$WebhookPort/health" -TimeoutSec 2
                if ($health.service -eq 'miawebhook') { $webhookReady = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 250
        }
        if (-not $webhookReady) {
            throw "MIA webhook did not start. Check the rules, secrets, port and Hermes gateway log."
        }
    }

    if ($GatewayOnly) {
        Write-Host "Hermes ready on $env:MIA_HERMES_URL. This process owns the Gateway."
        $service.WaitForExit()
    } else {
        if ($AppPath) { $exe = (Resolve-Path -LiteralPath $AppPath).Path }
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Run build.ps1 first.' }
        $miaArgs = @()
        if ($SelfTestTaskFile) { $miaArgs = @('--hermes-self-test-file', ('"{0}"' -f (Resolve-Path -LiteralPath $SelfTestTaskFile).Path)) }
        elseif ($WorkCenter) { $miaArgs = @('--work-center') }
        $mia = if ($miaArgs.Count) { Start-Process -FilePath $exe -ArgumentList $miaArgs -WorkingDirectory $workPath -PassThru }
            else { Start-Process -FilePath $exe -WorkingDirectory $workPath -PassThru }
        if ($loading) {
            # The pet signals readiness only after WPF renders its first frame.
            while (-not $loading.HasExited -and -not $mia.HasExited) { Start-Sleep -Milliseconds 100 }
            if ($mia.HasExited -and -not $loading.HasExited) { [IO.File]::WriteAllText($progressPath + '.ready', 'closed') }
        }
        $mia.WaitForExit()
        if ($SelfTestTaskFile) { Write-Host "MIA check result: $env:MIA_DATA_DIR\hermes-ui-check.json" }
        if ($mia.ExitCode -ne 0) { throw "MIA exited with code $($mia.ExitCode)." }
    }
} catch {
    if ($progressPath) { [IO.File]::WriteAllText($progressPath + '.ready', 'failed') }
    if ($ShowLaunchError) {
        Add-Type -AssemblyName PresentationFramework
        $null = [Windows.MessageBox]::Show($_.Exception.Message, 'MIA 3.2')
    }
    throw
} finally {
    if ($progressPath) {
        [IO.File]::WriteAllText($progressPath + '.ready', 'closed')
        if ($loading) { $null = $loading.WaitForExit(2000) }
        Remove-Item -LiteralPath $progressPath,($progressPath + '.ready') -ErrorAction SilentlyContinue
    }
    if ($service -and -not $service.HasExited) {
        # Stop only the process tree started by this launcher.
        & "$env:SystemRoot\System32\taskkill.exe" /pid $service.Id /t /f 2>$null | Out-Null
    }
    if ($launchLock) { $launchLock.Dispose() }
    if ($legacyLock) { $legacyLock.Dispose() }
}
