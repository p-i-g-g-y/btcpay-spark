namespace BTCPayServer.Plugins.Spark.Data.Entities;

/// <summary>
/// Maps a Spark-side Lightning receive request to a BTCPay invoice (and tracks polling state
/// for the <c>SparkInvoicePoller</c> fallback path).
/// </summary>
public class SparkLightningInvoiceEntity
{
    /// <summary>SSP request id returned from <c>wallet.CreateLightningInvoiceAsync</c>.</summary>
    public string RequestId { get; set; } = string.Empty;

    public string WalletId { get; set; } = string.Empty;

    /// <summary>Optional — wallets may create invoices outside of BTCPay's invoice flow.</summary>
    public string? BtcPayInvoiceId { get; set; }

    public string PaymentHash { get; set; } = string.Empty;

    public string Bolt11 { get; set; } = string.Empty;

    public long AmountSats { get; set; }

    public string? Memo { get; set; }

    /// <summary>Reserved for future stablecoin support. Always <c>"BTC"</c> in v1.</summary>
    public string Currency { get; set; } = "BTC";

    /// <summary>Reserved for future LRC-20 / USDB support. Null in v1.</summary>
    public string? TokenIdentifier { get; set; }

    public SparkLightningInvoiceStatus Status { get; set; } = SparkLightningInvoiceStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset LastPolledAt { get; set; } = DateTimeOffset.UtcNow;
    public int PollAttempt { get; set; }

    public string? MetadataJson { get; set; }
}
