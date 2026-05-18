#!/usr/bin/env bash
set -e

ROOT_DIR=$(pwd)
PLUGIN_DIR="BTCPayServer.Plugins.Spark"
OUTPUT_DIR="$ROOT_DIR/$PLUGIN_DIR/bin/Debug/net10.0"

if [ -d "$OUTPUT_DIR" ]; then
  echo "Cleaning $OUTPUT_DIR"
  rm -rf "$OUTPUT_DIR"
fi

if [ -z "${CI:-}" ]; then
  echo "Initialising and updating submodules..."
  git submodule update --init --recursive

  echo "Restoring workloads..."
  dotnet workload restore
fi

APPSETTINGS="submodules/btcpayserver/BTCPayServer/appsettings.dev.json"
if [ ! -f "$APPSETTINGS" ]; then
  echo "Creating $APPSETTINGS"
  echo '{ "DEBUG_PLUGINS": "../../../BTCPayServer.Plugins.Spark/bin/Debug/net10.0/BTCPayServer.Plugins.Spark.dll" }' > "$APPSETTINGS"
fi

echo "Publishing plugin..."
dotnet publish "$PLUGIN_DIR" -c Debug -o "$OUTPUT_DIR"

echo "Setup complete."
