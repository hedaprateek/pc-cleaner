param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$localSdk = Join-Path $taskRoot '.dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.cli'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& $sdk publish (Join-Path $taskRoot 'PcCleaner.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:RestoreConfigFile=$taskRoot\NuGet.Config" "-p:RestorePackagesPath=$taskRoot\.nuget" -o (Join-Path $taskRoot "dist\$Runtime")
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Portable app ready in $taskRoot\dist\$Runtime"
