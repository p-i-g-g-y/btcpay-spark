using BTCPayServer.Plugins.Spark.Data.Entities;

namespace BTCPayServer.Plugins.Spark.Models;

public class StoreOverviewViewModel
{
    public string StoreId { get; set; } = "";
    public string WalletId { get; set; } = "";
    public string SparkAddress { get; set; } = "";
    public string IdentityPublicKeyHex { get; set; } = "";
    public string? StaticDepositAddress { get; set; }
    public string Network { get; set; } = "";

    public SparkBalancesViewModel? Balances { get; set; }
    public bool IsLightningEnabled { get; set; }
    public bool CanManagePrivateKeys { get; set; }

    public IReadOnlyList<SparkActivityRow> RecentInvoiceRows { get; set; } = [];
    public IReadOnlyList<SparkActivityRow> RecentOutgoingPaymentRows { get; set; } = [];
    public IReadOnlyList<SparkActivityRow> RecentDepositRows { get; set; } = [];
    public IReadOnlyList<SparkActivityRow> RecentWithdrawalRows { get; set; } = [];

    public int RecentInvoiceCount { get; set; }
    public int RecentOutgoingPaymentCount { get; set; }
    public int RecentDepositCount { get; set; }
    public int RecentWithdrawalCount { get; set; }

    /// <summary>
    /// Top-of-feed slice from the SO-authoritative transfer history. Null when the SOs were
    /// unreachable (the view falls back gracefully). Populated by <c>SparkTransferActivityService</c>.
    /// </summary>
    public SparkActivityPage? RecentTransfers { get; set; }

    public IReadOnlyList<ServiceConnectionStatus> Services { get; set; } = [];
}
