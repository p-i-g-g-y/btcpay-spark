$ErrorActionPreference = "Stop"

$RootDir = (Get-Location).Path
$PluginDir = "BTCPayServer.Plugins.Spark"
$OutputDir = Join-Path $RootDir "$PluginDir\bin\Debug\net10.0"

if (Test-Path $OutputDir) {
    Write-Host "Cleaning $OutputDir"
    Remove-Item $OutputDir -Recurse -Force
}

if (-not $env:CI) {
    Write-Host "Initialising and updating submodules..."
    git submodule update --init --recursive

    Write-Host "Restoring workloads..."
    dotnet workload restore
}

$AppSettings = "submodules\btcpayserver\BTCPayServer\appsettings.dev.json"
if (-not (Test-Path $AppSettings)) {
    Write-Host "Creating $AppSettings"
    '{ "DEBUG_PLUGINS": "../../../BTCPayServer.Plugins.Spark/bin/Debug/net10.0/BTCPayServer.Plugins.Spark.dll" }' | Out-File -FilePath $AppSettings -Encoding utf8
}

Write-Host "Publishing plugin..."
dotnet publish $PluginDir -c Debug -o $OutputDir

Write-Host "Setup complete."
