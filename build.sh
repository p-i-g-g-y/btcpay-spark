#!/usr/bin/env bash
#
# Build script for BTCPayServer.Plugins.Spark.
# Produces ./artifacts/BTCPayServer.Plugins.Spark.btcpay ready for upload to BTCPay.
#
# See BUILD.md for the manual step-by-step equivalent and troubleshooting.
#
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "$0")" && pwd)
PLUGIN_DIR="$ROOT_DIR/BTCPayServer.Plugins.Spark"
CSPROJ="$PLUGIN_DIR/BTCPayServer.Plugins.Spark.csproj"
PUBLISH_DIR="$PLUGIN_DIR/bin/Release/publish"
ARTIFACTS_DIR="$ROOT_DIR/artifacts"
ARTIFACT="$ARTIFACTS_DIR/BTCPayServer.Plugins.Spark.btcpay"

if [ ! -f "$ROOT_DIR/submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj" ]; then
    echo "BTCPay submodule not initialised. Run:"
    echo "  git submodule update --init --recursive"
    echo "  git -C submodules/btcpayserver fetch --depth 1 origin tag v2.3.9"
    echo "  git -C submodules/btcpayserver checkout v2.3.9"
    exit 1
fi

echo "==> Cleaning previous publish output"
rm -rf "$PUBLISH_DIR"
mkdir -p "$ARTIFACTS_DIR"
rm -f "$ARTIFACT"

echo "==> Restoring NuGet packages"
dotnet restore "$CSPROJ"

echo "==> Publishing in Release configuration"
dotnet publish "$CSPROJ" -c Release -o "$PUBLISH_DIR" --no-restore

echo "==> Packaging .btcpay archive"
( cd "$PUBLISH_DIR" && zip -rq "$ARTIFACT" . -x "*.pdb" )

SIZE=$(du -h "$ARTIFACT" | awk '{print $1}')
echo
echo "Artifact: $ARTIFACT ($SIZE)"
echo "Upload it via BTCPay -> Server Settings -> Plugins -> Upload Plugin."
