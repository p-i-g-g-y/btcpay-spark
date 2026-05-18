$ErrorActionPreference = "Stop"

$RootDir = (Get-Location).Path
$PluginDir = Join-Path $RootDir "BTCPayServer.Plugins.Spark"
$Csproj = Join-Path $PluginDir "BTCPayServer.Plugins.Spark.csproj"
$PublishDir = Join-Path $PluginDir "bin\Release\publish"
$ArtifactsDir = Join-Path $RootDir "artifacts"
$Artifact = Join-Path $ArtifactsDir "BTCPayServer.Plugins.Spark.btcpay"

if (-not (Test-Path "$RootDir\submodules\btcpayserver\BTCPayServer\BTCPayServer.csproj")) {
    Write-Error @"
BTCPay submodule not initialised. Run:
  git submodule update --init --recursive
  git -C submodules/btcpayserver fetch --depth 1 origin tag v2.3.9
  git -C submodules/btcpayserver checkout v2.3.9
"@
    exit 1
}

Write-Host "==> Cleaning previous publish output"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ArtifactsDir | Out-Null
if (Test-Path $Artifact) { Remove-Item $Artifact -Force }

Write-Host "==> Restoring NuGet packages"
dotnet restore $Csproj

Write-Host "==> Publishing in Release configuration"
dotnet publish $Csproj -c Release -o $PublishDir --no-restore

Write-Host "==> Packaging .btcpay archive"
$Files = Get-ChildItem -Path $PublishDir -Recurse -File | Where-Object { $_.Extension -ne ".pdb" }
Compress-Archive -Path ($Files | ForEach-Object { $_.FullName }) -DestinationPath $Artifact -CompressionLevel Optimal

$Size = "{0:N1} MB" -f ((Get-Item $Artifact).Length / 1MB)
Write-Host ""
Write-Host "Artifact: $Artifact ($Size)"
Write-Host "Upload it via BTCPay -> Server Settings -> Plugins -> Upload Plugin."
