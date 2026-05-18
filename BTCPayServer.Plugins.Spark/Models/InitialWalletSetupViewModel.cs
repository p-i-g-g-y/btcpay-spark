namespace BTCPayServer.Plugins.Spark.Models;

public class InitialWalletSetupViewModel
{
    /// <summary>BIP-39 mnemonic. Leave empty to generate a fresh 12-word seed.</summary>
    public string? Mnemonic { get; set; }

    /// <summary>Optional BIP-39 passphrase. Encrypted at rest with the wallet secret.</summary>
    public string? Passphrase { get; set; }
}
