using BTCPayServer.Plugins.Spark.Data.Entities;

namespace BTCPayServer.Plugins.Spark.Models;

public class SparkActivityDashboardWidgetViewModel
{
    public string StoreId { get; set; } = "";
    public bool Configured { get; set; }
    public IReadOnlyList<SparkLightningInvoiceEntity> RecentInvoices { get; set; } = [];
    public IReadOnlyList<SparkWalletDepositEntity> RecentDeposits { get; set; } = [];
    public IReadOnlyList<SparkWalletWithdrawalEntity> RecentWithdrawals { get; set; } = [];
}
