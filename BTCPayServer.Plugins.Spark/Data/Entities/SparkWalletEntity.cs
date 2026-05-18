using NSpark;

namespace BTCPayServer.Plugins.Spark.Data.Entities;

/// <summary>
/// One Spark wallet per BTCPay store. The mnemonic is encrypted at rest via
/// <see cref="Microsoft.AspNetCore.DataProtection.IDataProtector"/> with purpose
/// <c>SparkPlugin.WalletSecret</c>.
/// </summary>
public class SparkWalletEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Owning BTCPay store id. Unique — one wallet per store.</summary>
    public string StoreId { get; set; } = string.Empty;

    public SparkNetwork Network { get; set; }

    /// <summary>BIP-39 mnemonic encrypted with the wallet secret protector.</summary>
    public string EncryptedMnemonic { get; set; } = string.Empty;

    /// <summary>Optional BIP-39 passphrase, also encrypted.</summary>
    public string? EncryptedPassphrase { get; set; }

    /// <summary>BIP-44 account index. Mainnet defaults to 1, Regtest to 0 (NSpark convention).</summary>
    public int Account { get; set; }

    /// <summary>The wallet's Spark address (<c>spark1...</c> / <c>sparkrt1...</c>) cached for display.</summary>
    public string SparkAddress { get; set; } = string.Empty;

    /// <summary>Identity public key (hex) — stable identifier across mnemonic re-imports.</summary>
    public string IdentityPublicKeyHex { get; set; } = string.Empty;

    /// <summary>Cached static deposit address; null until the admin requests one.</summary>
    public string? StaticDepositAddress { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastEventAt { get; set; }

    public string? MetadataJson { get; set; }
}
