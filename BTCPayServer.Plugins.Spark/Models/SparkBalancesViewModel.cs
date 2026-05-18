namespace BTCPayServer.Plugins.Spark.Models;

/// <summary>
/// Three-way Spark balance snapshot exposed in the dashboard widget and overview page.
/// Mirrors NSpark's <c>SatsBalance(Available, Owned, Incoming)</c>.
/// </summary>
public class SparkBalancesViewModel
{
    public long AvailableSats { get; set; }
    public long OwnedSats { get; set; }
    public long IncomingSats { get; set; }
    public int LeafCount { get; set; }

    public decimal AvailableBtc => Helpers.SparkAmountConverter.SatsToBtc(AvailableSats);
    public decimal OwnedBtc => Helpers.SparkAmountConverter.SatsToBtc(OwnedSats);
    public decimal IncomingBtc => Helpers.SparkAmountConverter.SatsToBtc(IncomingSats);
}
