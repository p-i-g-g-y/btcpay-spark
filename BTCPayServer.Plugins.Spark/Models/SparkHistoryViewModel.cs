using BTCPayServer.Plugins.Spark.Data.Entities;

namespace BTCPayServer.Plugins.Spark.Models;

public class SparkHistoryViewModel
{
    public string StoreId { get; set; } = "";

    /// <summary>Pre-projected activity rows for the local-DB tabs. Keeps row layout identical across tabs.</summary>
    public IReadOnlyList<SparkActivityRow> InvoiceRows { get; set; } = [];
    public IReadOnlyList<SparkActivityRow> OutgoingPaymentRows { get; set; } = [];
    public IReadOnlyList<SparkActivityRow> DepositRows { get; set; } = [];
    public IReadOnlyList<SparkActivityRow> WithdrawalRows { get; set; } = [];

    /// <summary>Raw entity counts kept so the tab headers can show "(n)" without re-counting projected rows.</summary>
    public int InvoiceCount { get; set; }
    public int OutgoingPaymentCount { get; set; }
    public int DepositCount { get; set; }
    public int WithdrawalCount { get; set; }

    /// <summary>SO-authoritative all-activity feed. Page-able via <c>?offset=</c> on the action.</summary>
    public SparkActivityPage? Transfers { get; set; }

    /// <summary>True when the user explicitly asked to include leaf-rebalancing swaps (advanced toggle).</summary>
    public bool ShowInternalSwaps { get; set; }
}
