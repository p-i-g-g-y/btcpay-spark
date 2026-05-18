using System.Globalization;
using System.Numerics;

namespace BTCPayServer.Plugins.Spark.Helpers;

/// <summary>
/// Amount conversions for the plugin. v1 deals in sats only; the BigInteger overloads exist so
/// future stablecoin support (e.g. USDB at 6-18 decimals) can reuse the same plumbing.
/// </summary>
public static class SparkAmountConverter
{
    public const int BtcDecimals = 8;

    public static decimal SatsToBtc(long sats) => decimal.Divide(sats, 100_000_000m);

    public static long BtcToSats(decimal btc) => (long)Math.Round(btc * 100_000_000m, MidpointRounding.AwayFromZero);

    public static string FormatSats(long sats) => sats.ToString("N0", CultureInfo.InvariantCulture);

    public static string FormatBtc(long sats, int decimals = BtcDecimals)
        => SatsToBtc(sats).ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>BigInteger-aware conversion for arbitrary-decimal tokens.</summary>
    public static decimal SmallestUnitsToDecimal(BigInteger amount, int decimals)
    {
        if (decimals == 0) return (decimal)amount;
        var divisor = BigInteger.Pow(10, decimals);
        var whole = amount / divisor;
        var fraction = amount % divisor;
        return (decimal)whole + (decimal)fraction / (decimal)divisor;
    }

    public static BigInteger DecimalToSmallestUnits(decimal value, int decimals)
    {
        if (decimals == 0) return new BigInteger(decimal.Truncate(value));
        var scaled = value * (decimal)BigInteger.Pow(10, decimals);
        return new BigInteger(decimal.Truncate(scaled));
    }
}
