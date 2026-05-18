namespace BTCPayServer.Plugins.Spark.Models;

public class SparkDashboardWidgetViewModel
{
    public string StoreId { get; set; } = "";
    public bool Configured { get; set; }
    public string Network { get; set; } = "";
    public SparkBalancesViewModel? Balances { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Latest 4 activity rows, newest first. Empty when quiet. Uses the canonical
    /// <see cref="SparkActivityRow"/> shape so the dashboard widget renders rows identical to
    /// what appears on Overview / History — just packed tighter to fit the widget card.
    /// </summary>
    public IReadOnlyList<SparkActivityRow> RecentActivity { get; set; } = [];
}
