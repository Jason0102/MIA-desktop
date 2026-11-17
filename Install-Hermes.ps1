param([switch]$SkipBrowser, [string]$CodexPath)

$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot '.mia-runtime'
$homePath = Join-Path $runtime 'hermes'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
$installer = Join-Path $runtime 'hermes-install-v0.23.1.ps1'
$portableInstaller = Join-Path $runtime 'hermes-install-portable.ps1'
$installerHash = '44BB5B021498C0BBA95F77AC30B6C4676F3FFB7AA92EA48C731CD0940E941CDC'
if (-not (Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/NousResearch/hermes-agent/v0.23.1/scripts/install.ps1' -OutFile $installer
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $installerHash) {
    throw 'The downloaded Hermes installer does not match the pinned SHA-256.'
}

# The upstream launcher is still generated inside this Hermes home. MIA calls it by absolute path.
$source = [IO.File]::ReadAllText($installer)
$registryWrite = '[Environment]::SetEnvironmentVariable("Path", "$binDir;$userPath", "User")'
if (-not $source.Contains($registryWrite)) { throw 'Cannot identify the pinned installer PATH registration.' }
$source = $source.Replace($registryWrite, '# MIA: keep user PATH unchanged.')
$source = $source.Replace('added $binDir to your user PATH (new shells pick it up)', 'MIA uses the local launcher at $binDir')
[IO.File]::WriteAllText($portableInstaller, $source, [Text.UTF8Encoding]::new($true))
$env:UV_CACHE_DIR = Join-Path $runtime 'uv-cache'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $runtime 'uv-python'

$installArgs = @{
    HermesHome = $homePath
    InstallDir = (Join-Path $homePath 'hermes-agent')
    Branch = 'main'
    Commit = '3b421578588243f920886e6fe79a6faa87ea026e'
    Channel = 'stable'
    NonInteractive = $true
    SkipBrowser = $SkipBrowser
}
& $portableInstaller @installArgs
if ($LASTEXITCODE -ne 0) { throw "Hermes installation failed (exit $LASTEXITCODE)." }

# Install the attachment/chat adapter into the same native Python used by Start-Mia.
$discordPython = Get-ChildItem -LiteralPath (Join-Path $homePath 'installs') -Directory |
    ForEach-Object {
        $record = Join-Path $_.FullName 'facts.json'
        if (Test-Path -LiteralPath $record) {
            $facts = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
            if ($facts.packages.venv.environment) { Join-Path $facts.packages.venv.environment 'Scripts/python.exe' }
        }
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
$discordUv = Get-ChildItem -LiteralPath (Join-Path $homePath 'tools') -Filter uv.exe -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $discordPython -or -not $discordUv) { throw 'Cannot locate the installed Hermes Python/uv runtime.' }
& $discordUv pip install --python $discordPython 'discord.py==2.7.1' 'audioop-lts==0.2.2'
if ($LASTEXITCODE -ne 0) { throw 'Discord adapter dependency installation failed.' }

# Reuse an installed native Codex CLI and its matching Windows helper binaries.
$codexSource = if ($CodexPath) { (Resolve-Path -LiteralPath $CodexPath).Path }
    else { (Get-Command codex.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source }
if ($codexSource -and [IO.Path]::GetFileName($codexSource) -ieq 'codex.exe') {
    $binPath = Join-Path $runtime 'bin'
    New-Item -ItemType Directory -Force -Path $binPath | Out-Null
    foreach ($name in @('codex.exe', 'codex-command-runner.exe', 'codex-windows-sandbox-setup.exe', 'codex-code-mode-host.exe')) {
        $sourcePath = Join-Path (Split-Path -Parent $codexSource) $name
        $destinationPath = Join-Path $binPath $name
        if ($sourcePath -ne $destinationPath -and (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        }
    }
} else {
    Write-Warning 'The Codex CLI was not found. Install it or pass -CodexPath before starting MIA.'
}
