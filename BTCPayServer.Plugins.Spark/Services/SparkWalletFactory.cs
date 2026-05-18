using NBitcoin;
using NSpark;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>BIP-39 mnemonic helpers + per-network account convention.</summary>
public static class SparkWalletFactory
{
    /// <summary>Generates a fresh 12-word BIP-39 English mnemonic.</summary>
    public static string GenerateMnemonic()
        => new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

    /// <summary>Validates a 12 or 24-word BIP-39 mnemonic and returns the canonical form (whitespace-normalised, lowercase).</summary>
    public static string NormalizeAndValidateMnemonic(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Mnemonic cannot be empty.");
        var trimmed = string.Join(' ',
            input.Trim().Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        var mnemonic = new Mnemonic(trimmed, Wordlist.English); // throws on invalid checksum / wordlist
        return mnemonic.ToString();
    }

    /// <summary>The default BIP-44 account index NSpark uses for a given network.</summary>
    public static int DefaultAccount(SparkNetwork network) =>
        network == SparkNetwork.Regtest ? 0 : 1;
}
