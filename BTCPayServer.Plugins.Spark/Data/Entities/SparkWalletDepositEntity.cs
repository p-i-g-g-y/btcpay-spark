namespace BTCPayServer.Plugins.Spark.Data.Entities;

/// <summary>
/// One row per UTXO discovered at the wallet's static deposit address. Records the two-step claim
/// state machine (<c>ClaimStaticDepositAsync</c> → <c>ClaimPendingTransfersAsync</c>) so that a
/// partial failure between steps can be resumed.
/// </summary>
/// <remarks>
/// Single-use deposit addresses (<c>GetDepositAddressAsync</c>) are intentionally NOT supported —
/// the plugin uses only the wallet's deterministic static deposit address per nspark guidance.
/// </remarks>
public class SparkWalletDepositEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string WalletId { get; set; } = string.Empty;

    /// <summary>The wallet's static deposit address (P2TR <c>bc1p…</c> / <c>bcrt1p…</c>).</summary>
    public string Address { get; set; } = string.Empty;

    public string TxId { get; set; } = string.Empty;
    public int Vout { get; set; }

    public long? AmountSats { get; set; }

    public SparkWalletDepositStatus Status { get; set; } = SparkWalletDepositStatus.Discovered;

    /// <summary>Transfer id returned by <c>ClaimStaticDepositAsync</c> (Spark transfer that holds the credited sats).</summary>
    public string? TransferId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SeenAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>Number of claim attempts so far. Informational only — the auto-claimer retries
    /// on every sweep regardless until the deposit reaches <c>Settled</c>.</summary>
    public int ClaimAttempt { get; set; }

    public string? LastError { get; set; }

    public string? MetadataJson { get; set; }
}
