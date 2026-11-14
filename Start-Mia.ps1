param(
    [switch]$Setup,
    [switch]$GatewayOnly,
    [switch]$ShowLaunchError,
    [string]$OpenAiKeyFile,
    [string]$CodexPath,
    [string]$Model,
    [string]$SelfTestTaskFile
)

$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot '.mia-runtime'
$homePath = Join-Path $runtime 'hermes'
$workPath = Join-Path $runtime 'work'
$exe = Join-Path $PSScriptRoot 'dist/MIA-3.2-win-x64/MIA.exe'
$service = $null
$launchLock = $null

function Invoke-Hermes {
    & $script:hermes @args
    if ($LASTEXITCODE -ne 0) { throw "Hermes command failed (exit $LASTEXITCODE)." }
}

try {
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    $launchLock = [IO.File]::Open((Join-Path $runtime 'launch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $env:HERMES_HOME = $homePath
    $commandCandidates = @('bin/hermes.exe', 'bin/hermes.cmd', 'hermes-agent/.hermes/bin/hermes.exe', 'hermes-agent/.hermes/bin/hermes.cmd')
    $hermes = $commandCandidates | ForEach-Object { Join-Path $homePath $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $hermes) { throw 'Run Install-Hermes.ps1 first.' }
    $keyStore = Join-Path $runtime 'openai.key'
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
    foreach ($relative in @('work/input','work/jobs','mia-data','hermes/plugins/mia-codex')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $runtime $relative) | Out-Null
    }
    $soul = Join-Path $homePath 'SOUL.md'
    if (-not (Test-Path -LiteralPath $soul) -or [IO.File]::ReadAllText($soul).StartsWith('You are Hermes Agent, built by Nous Research. Be direct:')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'integration/hermes/SOUL.md') -Destination $soul
    }
    $plugin = Join-Path $homePath 'plugins/mia-codex'
    foreach ($name in @('plugin.yaml','__init__.py','bridge.py','extensions.py')) {
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
    $env:MIA_DATA_DIR = Join-Path $runtime 'mia-data'
    if ($SelfTestTaskFile) { $env:MIA_DATA_DIR = Join-Path $runtime ('checks/' + [guid]::NewGuid().ToString('N')) }
    # Keep the API tool selection local to this profile and avoid starting other adapters.
    Invoke-Hermes config set platform_toolsets.api_server '[hermes-api-server, computer_use, mia_codex]'
    $arguments = @('gateway','run')
    $gatewayExe = $hermes
    if ([IO.Path]::GetExtension($hermes) -eq '.cmd') {
        $gatewayExe = $env:ComSpec
        $arguments = @('/d','/s','/c', ('""{0}" gateway run"' -f $hermes))
    }
    $service = Start-Process -FilePath $gatewayExe -ArgumentList $arguments -WorkingDirectory $workPath -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runtime 'hermes-gateway.log') -RedirectStandardError (Join-Path $runtime 'hermes-gateway-error.log')
    $headers = @{ Authorization = "Bearer $env:API_SERVER_KEY" }
    $ready = $false
    for ($attempt = 0; $attempt -lt 120 -and -not $service.HasExited; $attempt++) {
        try {
            $null = Invoke-RestMethod -Uri "$env:MIA_HERMES_URL/v1/toolsets" -Headers $headers -TimeoutSec 2
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 400 }
    }
    if (-not $ready) { throw 'Hermes did not start. Check .mia-runtime/hermes-gateway-error.log.' }

    if ($GatewayOnly) {
        Write-Host "Hermes ready on $env:MIA_HERMES_URL. This process owns the Gateway."
        $service.WaitForExit()
    } else {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Run build.ps1 first.' }
        $miaArgs = @()
        if ($SelfTestTaskFile) { $miaArgs = @('--hermes-self-test-file', ('"{0}"' -f (Resolve-Path -LiteralPath $SelfTestTaskFile).Path)) }
        $mia = if ($miaArgs.Count) { Start-Process -FilePath $exe -ArgumentList $miaArgs -WorkingDirectory $PSScriptRoot -PassThru }
            else { Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -PassThru }
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
}
