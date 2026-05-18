# Changelog

## [0.1.0-alpha.2] - 2026-05-18

### Added
- GitHub Actions workflow (`.github/workflows/release.yml`) that builds `BTCPayServer.Plugins.Spark.btcpay` and attaches it to the GitHub release automatically when a release is published.

## [0.1.0-alpha.1] - 2026-05-18

### Added
- Initial plugin scaffold targeting BTCPay v2.3.9 (`net10.0`): solution, `BTCPayServer.Plugins.Spark` project, `SparkPluginDbContext` with entities for wallets, Lightning invoices, static-deposit UTXOs, on-chain wallet withdrawals, and event cursors.
- Network configuration loader supporting Spark mainnet/regtest presets with `spark.json` overrides.
- Setup helpers (`setup.sh`, `setup.ps1`), build script (`build.sh`, `build.ps1`) that produces `artifacts/BTCPayServer.Plugins.Spark.btcpay`, and migration helpers (`add-migration.sh`, `add-migration.ps1`).
- `BUILD.md` documenting the full build/package/upload pipeline.
- Wallet creation wizard, store overview, receive (fund) page, unified send page (BOLT11 / Lightning address / on-chain withdraw), history, settings, sidebar nav, dashboard widgets.
- `SparkLightningClient : IExtendedLightningClient` + connection-string handler + refcounted invoice listener + `IInvoiceMappingService` mapping persistence.
- `SparkEventSubscriber` (per-wallet `SubscribeEventsAsync` loop with exponential reconnect), `SparkInvoicePoller` (fallback polling with backoff curve ported from piggy-backend), `SparkDepositAutoClaimer` implementing the canonical two-step static-deposit claim flow: `ClaimStaticDepositAsync` → `ClaimPendingTransfersAsync`.
- Mnemonic encryption at rest via `IDataProtector` purpose `SparkPlugin.WalletSecret`.

### Pre-release operations
- **No EF Core migrations yet.** First boot runs `SparkPluginSchemaInitializer` (an `IStartupTask`) which generates the CREATE DDL from the live entity model via `Database.GenerateCreateScript()` and runs it against BTCPay's PostgreSQL database — only if our schema doesn't already exist. To iterate on the schema during development drop the schema (`DROP SCHEMA "BTCPayServer.Plugins.Spark" CASCADE;`) and restart BTCPay. Migrations will be introduced when the plugin moves to external users.

### Design notes
- **Static-only deposit funding.** The plugin uses only `GetStaticDepositAddressAsync` (one deterministic address per wallet, reusable for many UTXOs). Single-use deposit addresses (`GetDepositAddressAsync`) are intentionally not supported.
- **Spark address is receive-only.** It is shown on the Receive page so external Spark wallets can fund this one, but is not accepted as a destination in the admin Send form (no Spark-to-Spark send flow in v1).
- **Lightning-only as a BTCPay payment method.** No `IPaymentMethodHandler` / `IPayoutHandler` is registered — Spark plugs in as a Lightning provider via `type=spark;wallet-id={id}`.
