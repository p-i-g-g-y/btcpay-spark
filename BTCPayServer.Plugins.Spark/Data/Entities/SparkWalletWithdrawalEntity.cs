namespace BTCPayServer.Plugins.Spark.Data.Entities;

/// <summary>
/// Wallet-management on-chain withdrawal (admin sends sats from the Spark wallet to an external
/// L1 BTC address). Does NOT participate in BTCPay's payout flow.
/// </summary>
public class SparkWalletWithdrawalEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string WalletId { get; set; } = string.Empty;

    public string DestinationAddress { get; set; } = string.Empty;

    public long AmountSats { get; set; }

    public long? FeeQuoteSats { get; set; }
    public long? ActualFeeSats { get; set; }

    /// <summary>L1 transaction id once broadcast.</summary>
    public string? TxId { get; set; }

    public SparkWalletWithdrawalStatus Status { get; set; } = SparkWalletWithdrawalStatus.Pending;

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>BTCPay user id who initiated the withdrawal (audit trail).</summary>
    public string? InitiatedByUser { get; set; }

    public string? MetadataJson { get; set; }
}
