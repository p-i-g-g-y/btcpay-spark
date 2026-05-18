# Spark for BTCPay Server

> Accept Bitcoin Lightning payments through [Spark](https://www.spark.money) — a self-custodial Bitcoin Layer 2 — directly inside BTCPay Server.

Spark is a non-custodial Bitcoin scaling protocol built on FROST threshold signatures. This plugin gives each BTCPay store its own per-store Spark wallet and registers Spark as a Lightning provider so customer invoices can be paid over Lightning instantly without a separate Lightning node, NBXplorer, or Boltz.

Built on **[NSpark](https://github.com/piggy/nspark)**, a native .NET SDK for the Spark protocol — no JS shim, no FFI wrapper. NSpark speaks Spark's gRPC + GraphQL directly and ships the FROST native binding for Linux / macOS / Windows on x64 and arm64.

## Scope

- **Customer-facing invoices**: Lightning only (BOLT11), backed by Spark's SSP.
- **Wallet management** (admin only): on-chain BTC deposits to fund the wallet, and on-chain withdrawals to an external Bitcoin address.
- **No Spark on-chain payment method** for invoices. On-chain receive/withdraw are admin actions on the wallet itself.

USD and stablecoin support (e.g. Flashnet USDB, LRC-20 tokens) are out of scope for v1; schema hooks (`Currency`, `TokenIdentifier`) are reserved for a future release.

## Getting Started

### Prerequisites

- .NET SDK 10.0.x
- A PostgreSQL-backed BTCPay Server 2.3.4+ (developed against v2.3.9)
- Spark mainnet credentials (defaults to the public Spark Signing Operators), or a regtest Spark stack for development.

### Build the uploadable artifact

```bash
git clone https://github.com/piggy/btcpay-spark.git
cd btcpay-spark
git submodule update --init --recursive
git -C submodules/btcpayserver checkout v2.3.9
./build.sh           # produces artifacts/BTCPayServer.Plugins.Spark.btcpay
```

Upload `artifacts/BTCPayServer.Plugins.Spark.btcpay` via **Server Settings → Plugins → Upload Plugin** in your BTCPay instance, then restart BTCPay.

See [`BUILD.md`](BUILD.md) for the full build process, troubleshooting, and how to target a different BTCPay version.

### Local development (no rebuild loop)

`./setup.sh` initialises the submodule, restores workloads, and wires up `DEBUG_PLUGINS` so BTCPay loads the plugin directly from `BTCPayServer.Plugins.Spark/bin/Debug/net10.0/`. Useful when iterating on the plugin without rebuilding the `.btcpay` archive each time.

### Running BTCPay locally with the plugin loaded

Run BTCPay from the submodule, pointing `DEBUG_PLUGINS` at the published plugin DLL:

```bash
cd submodules/btcpayserver/BTCPayServer
dotnet run
```

The Spark plugin will appear under each store's sidebar after installation.

### Configuration overrides

By default, the plugin selects the Spark network preset matching BTCPay's chain:

| BTCPay chain | Spark network |
|---|---|
| Mainnet | `SparkNetwork.Mainnet` (Spark public Signing Operators) |
| Regtest | `SparkNetwork.Regtest` (localhost:9001–9003) |
| Other | plugin disabled |

To override the SSP URL or signing operators, drop a `spark.json` file in BTCPay's data directory:

```json
{
  "SspUrl": "https://api.lightspark.com/graphql/spark/2025-03-19",
  "SspIdentityPublicKeyHex": "023e33e2920326f64ea31058d44777442d97d7d5cbfcf54e3060bc1695e5261c93",
  "SigningOperators": [
    { "Address": "https://0.spark.lightspark.com", "Identifier": "0000000000000000000000000000000000000000000000000000000000000001", "IdentityPublicKeyHex": "03dfbdff4b6332c220f8fa2ba8ed496c698ceada563fa01b67d9983bfc5c95e763" }
  ]
}
```

### Plugin kill switch

Set `BTCPAY_SPARK_DISABLED=true` to skip plugin registration entirely.

## Development

### Schema iteration during development

The plugin is **pre-release** and does not use EF Core migrations yet. On first install, `SparkPluginSchemaInitializer` runs once and creates the `BTCPayServer.Plugins.Spark` PostgreSQL schema directly from the live entity model (no version history). To iterate on the schema between rebuilds:

```sql
DROP SCHEMA "BTCPayServer.Plugins.Spark" CASCADE;
```

Then restart BTCPay. The schema initializer will recreate everything from the updated model on next boot.

When the plugin ships to external users we'll generate a baseline EF migration matching the then-current model and swap the initializer for `Database.MigrateAsync()`.

### Repository layout

```
BTCPayServer.Plugins.Spark/        # plugin project
  SparkPlugin.cs                   # entry point
  Configuration/                   # network presets + spark.json loader
  Controllers/                     # SparkController (initial setup, fund, withdraw, etc.)
  Data/                            # DbContext + entities + first-install schema initializer
  Lightning/                       # ILightningClient + connection string handler + listener
  Services/                        # wallet provider, event subscriber, invoice poller, etc.
  Views/                           # Razor pages (sidebar + dashboard + admin flows)
submodules/btcpayserver/           # git submodule, pinned to >= 2.3.4
```

## Known Limitations

- Native `libspark_frost.*` ships only for Debian-glibc, macOS, and Windows. Alpine/musl deployments are not supported yet.
- Spark tokens (LRC-20 / USDB) write API is not implemented upstream in `nspark`; this plugin reserves schema fields but does not expose token send/receive in v1.

## License

MIT — see [LICENSE](LICENSE).
