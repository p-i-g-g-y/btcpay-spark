namespace BTCPayServer.Plugins.Spark.Models;

public class SparkSettingsViewModel
{
    public string StoreId { get; set; } = "";
    public string WalletId { get; set; } = "";
    public string Network { get; set; } = "";
    public string SparkAddress { get; set; } = "";
    public string IdentityPublicKeyHex { get; set; } = "";
    public int Account { get; set; }

    /// <summary>Server-side privacy flag from the Signing Operators (null if we couldn't query it).</summary>
    public bool? PrivacyEnabled { get; set; }
    public string? PrivacyError { get; set; }

    /// <summary>Set only when the admin actively requested a seed reveal.</summary>
    public string? RevealedMnemonic { get; set; }

    public bool CanManagePrivateKeys { get; set; }
}
