namespace BTCPayServer.Plugins.Spark.Data.Entities;

/// <summary>
/// One row per outgoing Lightning payment attempted by the wallet. Persisted up front when
/// <c>SparkLightningClient.Pay</c> submits a BOLT11 to the SSP so that <c>GetPayment</c> queries
/// (driven by BTCPay's <c>LightningPendingPayoutListener</c>) and the background
/// <c>SparkOutgoingPaymentPoller</c> can both reconcile against the same record.
/// </summary>
/// <remarks>
/// Idempotency: <see cref="PaymentHash"/> is unique per <see cref="WalletId"/>. If <c>Pay</c> is
/// retried for the same BOLT11 (BTCPay's payout processor can re-invoke after restarts) the
/// existing row is reused — no duplicate SSP send is issued.
/// </remarks>
public class SparkOutgoingPaymentEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string WalletId { get; set; } = string.Empty;

    /// <summary>SHA-256 of the HTLC preimage (lowercase hex, 64 chars). Unique per wallet.</summary>
    public string PaymentHash { get; set; } = string.Empty;

    /// <summary>The BOLT11 invoice we paid (or attempted to pay).</summary>
    public string Bolt11 { get; set; } = string.Empty;

    /// <summary>SSP-side request identifier returned by <c>PayLightningInvoiceAsync</c>. Used purely for log correlation.</summary>
    public string? SspRequestId { get; set; }

    /// <summary>Invoice amount in sats (extracted from BOLT11; null only for amountless invoices we didn't override).</summary>
    public long? AmountSats { get; set; }

    /// <summary>The fee cap passed to <c>PayLightningInvoiceAsync</c>.</summary>
    public long? MaxFeeSats { get; set; }

    /// <summary>Actual fee in sats reported by the SSP after settlement. Null while pending.</summary>
    public long? ActualFeeSats { get; set; }

    /// <summary>HTLC preimage (lowercase hex, 64 chars) reported by the SSP after settlement. Null while pending.</summary>
    public string? Preimage { get; set; }

    public SparkOutgoingPaymentStatus Status { get; set; } = SparkOutgoingPaymentStatus.Pending;

    /// <summary>Last error string surfaced from <c>PayLightningInvoiceAsync</c> or the poller. Cleared on success.</summary>
    public string? LastError { get; set; }

    /// <summary>Memo / description parsed from the BOLT11 (optional, best-effort).</summary>
    public string? Memo { get; set; }

    /// <summary>Reserved for future stablecoin support. Always <c>"BTC"</c> in v1.</summary>
    public string Currency { get; set; } = "BTC";

    /// <summary>Reserved for future LRC-20 / USDB support. Null in v1.</summary>
    public string? TokenIdentifier { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>BOLT11-declared expiry. Used to bound polling — once past expiry+grace, mark Failed if still pending.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset LastPolledAt { get; set; } = DateTimeOffset.UtcNow;
    public int PollAttempt { get; set; }

    /// <summary>Optional originating BTCPay payout id, if the caller (BTCPay's payout processor) supplied one.</summary>
    public string? BtcPayPayoutId { get; set; }

    public string? MetadataJson { get; set; }
}
