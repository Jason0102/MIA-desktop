param([switch]$Test, [string]$OutputDirectory='dist/MIA-3.2-win-x64', [string]$DotnetPath)
$ErrorActionPreference='Stop'
$projectRoot=$PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.tools/home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$localSdk=Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetCommand=if($DotnetPath){(Resolve-Path -LiteralPath $DotnetPath).Path}elseif(Test-Path -LiteralPath $localSdk){$localSdk}else{'dotnet'}
if($Test){
    & $dotnetCommand run --project (Join-Path $projectRoot 'tests/Mia.Checks.csproj') -c Release
    if($LASTEXITCODE -ne 0){throw 'Tests failed.'}
}
& $dotnetCommand publish (Join-Path $projectRoot 'src/Mia/Mia.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -o (Join-Path $projectRoot $OutputDirectory)
if($LASTEXITCODE -ne 0){throw 'Publish failed.'}
Write-Host "Ready: $OutputDirectory/MIA.exe"
