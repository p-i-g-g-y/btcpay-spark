# Build & Package — BTCPayServer.Plugins.Spark

This document is the source of truth for building the Spark plugin into an uploadable `.btcpay` artifact. It is written for both humans and automated agents.

## TL;DR

```bash
# 1. Clone + init submodule (first time only)
git submodule update --init --recursive
git -C submodules/btcpayserver fetch --depth 1 origin tag v2.3.9
git -C submodules/btcpayserver checkout v2.3.9

# 2. Build the artifact
./build.sh

# 3. Upload artifacts/BTCPayServer.Plugins.Spark.btcpay
#    via BTCPay → Server Settings → Plugins → Upload Plugin
```

The output is `artifacts/BTCPayServer.Plugins.Spark.btcpay` (~24 MB).

---

## Prerequisites

| Tool | Version | Why |
|---|---|---|
| .NET SDK | **10.0.x** | BTCPay v2.3.9 targets `net10.0` — see `submodules/btcpayserver/Build/Common.csproj`. The plugin must match. |
| Git | 2.x | For the BTCPay submodule and (optionally) the plugin repo. |
| `zip` | any | Used by `build.sh` to assemble the `.btcpay` archive. macOS/Linux ship it; on Windows use `Compress-Archive` (see `build.ps1`). |

Verify with:

```bash
dotnet --list-sdks    # must include a 10.0.x line
git --version
```

`global.json` pins `version: "10.0.102"` with `rollForward: latestFeature`, so any installed 10.0.x SDK in the same feature band will resolve.

### Targeting a different BTCPay version

The plugin's `BTCPayServer.Plugins.Spark.csproj` references BTCPay via project reference into `submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj`. To target a newer BTCPay:

```bash
git -C submodules/btcpayserver fetch --depth 1 origin tag vX.Y.Z
git -C submodules/btcpayserver checkout vX.Y.Z
```

If the new BTCPay version changes its `TargetFramework` (e.g. `net11.0`), update **both** the plugin's `<TargetFramework>` in `BTCPayServer.Plugins.Spark/BTCPayServer.Plugins.Spark.csproj` and the `version` in `global.json`, then re-run `./build.sh`.

If BTCPay bumps its `NBitcoin`, `BTCPayServer.Lightning.Common`, `Microsoft.EntityFrameworkCore.*`, or `Npgsql.EntityFrameworkCore.PostgreSQL` package versions, update them in the plugin csproj to match. Mismatches cause `MethodNotFoundException` or `TypeLoadException` at plugin load time.

---

## Step-by-step build

### 1. Initialise the BTCPay submodule

The plugin references BTCPay via a git submodule pinned to a tag. If you cloned this repo with `--recurse-submodules`, skip to step 2.

```bash
# Register the submodule (only if it isn't yet populated)
git submodule update --init --recursive

# Pin to a known-good BTCPay release tag (>= 2.3.4)
git -C submodules/btcpayserver fetch --depth 1 origin tag v2.3.9
git -C submodules/btcpayserver checkout v2.3.9
```

`v2.3.9` is the version this plugin is currently developed against. Any 2.3.x patch release should work but is not regression-tested.

### 2. Restore NuGet packages

```bash
dotnet restore BTCPayServer.Plugins.Spark/BTCPayServer.Plugins.Spark.csproj
```

The first restore downloads ~600 MB of NuGet packages including BTCPay's transitive deps and `NSpark` (which carries native `libspark_frost.*` binaries for linux-x64/arm64, osx-x64/arm64, win-x64/arm64).

**Expected warning** (safe to ignore):

```
NU1608: Detected package version outside of dependency constraint:
Microsoft.CodeAnalysis.Workspaces.MSBuild 5.0.0 requires
Microsoft.CodeAnalysis.Workspaces.Common (= 5.0.0) but version 5.3.0 was resolved.
```

The 5.3.0 resolution is correct — it comes from EF Core 10.0.6 and is pinned explicitly in the csproj. The warning persists because a sibling Roslyn package wants the older version; the runtime works either way.

### 3. Build in Release mode

```bash
dotnet build BTCPayServer.Plugins.Spark/BTCPayServer.Plugins.Spark.csproj -c Release --no-restore
```

Output: `BTCPayServer.Plugins.Spark/bin/Release/net10.0/BTCPayServer.Plugins.Spark.dll`.

### 4. Publish (gathers all bundled assemblies + native libs)

```bash
dotnet publish BTCPayServer.Plugins.Spark/BTCPayServer.Plugins.Spark.csproj \
  -c Release \
  -o BTCPayServer.Plugins.Spark/bin/Release/publish \
  --no-restore
```

The `publish` step is what actually drops the runtime closure (NSpark, NBitcoin, EF Core, BTCPayServer.Lightning.Common, plus `runtimes/{rid}/native/` libspark_frost binaries) next to the plugin DLL.

Two `<Private>` rules in the csproj keep BTCPay's own host assemblies **out** of this output (they're supplied by the running BTCPay server):

```xml
<ProjectReference Include="..\submodules\btcpayserver\BTCPayServer\BTCPayServer.csproj">
    <Private>false</Private>
    <Private Condition="'$(Configuration)' == 'Debug'">true</Private>
</ProjectReference>
```

In `Debug` they're included so the project can be run standalone for unit tests. In `Release` (used for the uploadable artifact) they're excluded.

### 5. Package as `.btcpay`

A `.btcpay` file is **just a zip** of the publish directory contents (BTCPay extracts it via `ZipFile.ExtractToDirectory`, see `submodules/btcpayserver/BTCPayServer/Plugins/PluginManager.cs:475`).

The filename must match the plugin's `Identifier`, which defaults to the assembly name. For us that's `BTCPayServer.Plugins.Spark`, so the artifact is `BTCPayServer.Plugins.Spark.btcpay`.

```bash
mkdir -p artifacts
rm -f artifacts/BTCPayServer.Plugins.Spark.btcpay
( cd BTCPayServer.Plugins.Spark/bin/Release/publish \
  && zip -rq ../../../../artifacts/BTCPayServer.Plugins.Spark.btcpay . -x "*.pdb" )
```

The `-x "*.pdb"` flag strips debug symbols (~3 MB savings; not needed at runtime).

### 6. Verify

```bash
unzip -l artifacts/BTCPayServer.Plugins.Spark.btcpay | head -30
unzip -l artifacts/BTCPayServer.Plugins.Spark.btcpay | grep -E "Spark\.dll|spark_frost|NSpark\.dll"
```

Expected hits:

- `BTCPayServer.Plugins.Spark.dll` — the plugin assembly
- `NSpark.dll` — Spark client library
- `runtimes/{linux-x64,linux-arm64,osx-x64,osx-arm64,win-x64,win-arm64}/native/libspark_frost.*` — native FROST binaries

If any of those are missing, the publish step was not done in `Release` configuration or `PrivateAssets=none` got stripped from the `NSpark` reference.

---

## One-shot build script

A convenience wrapper that runs steps 2–5 is provided as `build.sh` (and `build.ps1` for Windows). Run from the repo root:

```bash
./build.sh
```

It deletes any previous publish output, re-runs the full pipeline, and writes the artifact to `artifacts/BTCPayServer.Plugins.Spark.btcpay`. Safe to run repeatedly; idempotent.

---

## Installing into BTCPay

### Option A — Upload via the UI (production-friendly)

1. Go to **Server Settings → Plugins** in your BTCPay instance.
2. Scroll to **Upload Plugin** and select `artifacts/BTCPayServer.Plugins.Spark.btcpay`.
3. Click **Upload**, then restart BTCPay (the plugin loader processes pending installs at boot).
4. After restart, "Spark" appears under each store's sidebar once configured.

### Option B — `DEBUG_PLUGINS` (local development)

When iterating locally without rebuilding the `.btcpay` archive, point BTCPay at the published DLL directly:

```bash
echo '{ "DEBUG_PLUGINS": "../../../BTCPayServer.Plugins.Spark/bin/Debug/net10.0/BTCPayServer.Plugins.Spark.dll" }' \
  > submodules/btcpayserver/BTCPayServer/appsettings.dev.json

dotnet run --project submodules/btcpayserver/BTCPayServer
```

(The `setup.sh` script in this repo automates this dev workflow.)

### Option C — Drop into BTCPay's data directory

For headless / scripted installs without using the UI, drop the `.btcpay` file directly into BTCPay's plugin directory (typically `~/.btcpayserver/plugins/` on Linux, or wherever `BTCPAY_DATADIR/plugins` points) and restart BTCPay. The loader extracts and registers it on next boot.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `NETSDK1045: The current .NET SDK does not support targeting .NET 10.0.` | .NET 10 SDK not installed. | Install .NET 10 SDK or align `global.json`'s `version` to an SDK you have, then update `<TargetFramework>` if you're targeting a different BTCPay version. |
| `NU1107: Version conflict detected for Microsoft.CodeAnalysis.Common.` | EF Core 10.0.6 ships with newer Roslyn than its sibling `Microsoft.CodeAnalysis.CSharp.Workspaces` declares. | The csproj already pins all three `Microsoft.CodeAnalysis.*` packages to `5.3.0`. If the conflict re-surfaces after a BTCPay or EF Core bump, re-check those pins. |
| Plugin loads but tables aren't created. | `SparkPluginSchemaInitializer` only runs once per database. | Check the BTCPay logs for `Bootstrapping Spark plugin schema` on first boot. If you need a clean slate during development, run `DROP SCHEMA "BTCPayServer.Plugins.Spark" CASCADE;` against the BTCPay database and restart. |
| `DllNotFoundException: spark_frost` at runtime. | The native binary for the host RID didn't ship in the artifact. | Re-run `./build.sh` in `Release` and confirm `runtimes/{rid}/native/libspark_frost.*` is inside the `.btcpay` zip via step 6. |
| `Could not load file or assembly 'NBitcoin' ...` | BTCPay's `NBitcoin` version drifted from the version pinned in the plugin csproj. | Re-read `submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj` and update the plugin's `<PackageReference Include="NBitcoin" Version="..." />` to match. |
| Alpine / musl deployments fail with `libspark_frost.so: not found`. | NSpark ships glibc-only native binaries. | Not supported in v1. Use a glibc-based base image (Debian, Ubuntu, official BTCPay images). |
