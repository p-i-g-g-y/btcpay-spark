namespace BTCPayServer.Plugins.Spark.Data.Entities;

/// <summary>
/// Per-wallet bookkeeping for <c>SparkEventSubscriber</c>: tracks the last time we successfully
/// connected / received an event, and how many consecutive subscription failures we've hit
/// (drives exponential reconnect backoff).
/// </summary>
public class SparkEventCursorEntity
{
    public string WalletId { get; set; } = string.Empty;

    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastTransferAt { get; set; }
    public DateTimeOffset? LastDepositAt { get; set; }

    public int ConsecutiveFailures { get; set; }
}
