namespace BTCPayServer.Plugins.Spark.Configuration;

/// <summary>
/// Plugin-wide runtime knobs. Bound from <c>SparkPlugin</c> section of BTCPay configuration
/// (or left at defaults). Values that need per-network differentiation live in
/// <see cref="SparkNetworkConfig"/> instead.
/// </summary>
public sealed class SparkPluginOptions
{
    /// <summary><see cref="Microsoft.AspNetCore.DataProtection.IDataProtector"/> purpose string for wallet secrets.</summary>
    public const string WalletEncryptionPurpose = "SparkPlugin.WalletSecret";

    /// <summary>Tick interval for <c>SparkInvoicePoller</c>. Default: 5s.</summary>
    public TimeSpan InvoicePollerTick { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Number of fast-poll attempts before exponential backoff kicks in. Default: 10.</summary>
    public int InvoiceFastPollAttempts { get; set; } = 10;

    /// <summary>Cap for the exponential backoff per pending invoice. Default: 5min.</summary>
    public TimeSpan InvoiceMaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often <c>SparkDepositAutoClaimer</c> runs its safety sweep. Default: 60s.</summary>
    public TimeSpan DepositSweepInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum SSP fee (in sats) the auto-claimer will accept on a static deposit claim. Mirrors
    /// the JS SDK's <c>claimStaticDepositWithMaxFee(maxFee)</c> safety check: we fetch the UTXO
    /// amount from mempool.space, the SSP credit quote, and compute <c>fee = utxo - credit</c>;
    /// if it exceeds this cap, the claim is skipped and the admin can review in the History page.
    /// Set very high (e.g., 100_000_000) to effectively disable the cap. Default 5000 sats.
    /// </summary>
    public long MaxClaimFeeSats { get; set; } = 5_000;

    /// <summary>
    /// How long <c>SparkLightningClient.Pay</c> waits for the SSP to flip an outgoing send to a
    /// terminal status (Succeeded/Failed) before returning <c>PayResult.Unknown</c>. This blocking
    /// wait dramatically improves payout UX in BTCPay: most Spark Lightning sends complete in
    /// 2-5 s, so the payout flips straight to <c>Completed</c> instead of sitting in
    /// <c>InProgress</c> waiting for <c>LightningPendingPayoutListener</c>'s 10-minute reconcile.
    /// Default 30s. Set very small (e.g. 1s) to keep the legacy "always Unknown" behaviour.
    /// </summary>
    public TimeSpan OutgoingPaymentWaitForTerminal { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Polling interval used by <c>SparkLightningClient.Pay</c> while waiting for terminal status.
    /// Independent of <see cref="InvoicePollerTick"/> because this poll runs synchronously on a
    /// request thread and we want a tighter loop. Default 1s.
    /// </summary>
    public TimeSpan OutgoingPaymentWaitPollInterval { get; set; } = TimeSpan.FromSeconds(1);
}
