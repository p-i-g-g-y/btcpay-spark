using BTCPayServer.Plugins.Spark.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>Encrypts/decrypts wallet secrets (mnemonic, passphrase) at rest.</summary>
public interface IWalletSecretProtector
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}

public class DataProtectorWalletSecretProtector : IWalletSecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectorWalletSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(SparkPluginOptions.WalletEncryptionPurpose);
    }

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);
    public string Decrypt(string ciphertext) => _protector.Unprotect(ciphertext);
}
