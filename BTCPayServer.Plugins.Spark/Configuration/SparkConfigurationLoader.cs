using System.Text.Json;
using BTCPayServer.Configuration;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NSpark;

namespace BTCPayServer.Plugins.Spark.Configuration;

/// <summary>
/// Resolves the active <see cref="SparkNetworkConfig"/> by combining BTCPay's chain selection
/// (<see cref="DefaultConfiguration.GetNetworkType"/>) with an optional <c>{DataDir}/spark.json</c>
/// file override. Returns null for unsupported BTCPay chains (testnet, signet, mutiny) so the
/// plugin can short-circuit registration with a clear log message.
/// </summary>
public static class SparkConfigurationLoader
{
    private const string ConfigFileName = "spark.json";

    public static SparkNetworkConfig? Load(IConfiguration configuration)
    {
        var networkType = DefaultConfiguration.GetNetworkType(configuration);
        var preset = GetPreset(networkType);
        if (preset is null) return null;

        var dataDir = new DataDirectories().Configure(configuration).DataDir;
        var path = Path.Combine(dataDir, ConfigFileName);
        if (!File.Exists(path)) return preset;

        var json = File.ReadAllText(path);
        var fileConfig = JsonSerializer.Deserialize<FileConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (fileConfig is null) return preset;

        return preset with
        {
            SspUrl = string.IsNullOrWhiteSpace(fileConfig.SspUrl) ? preset.SspUrl : fileConfig.SspUrl,
            SspIdentityPublicKeyHex = string.IsNullOrWhiteSpace(fileConfig.SspIdentityPublicKeyHex)
                ? preset.SspIdentityPublicKeyHex
                : fileConfig.SspIdentityPublicKeyHex,
            SigningOperators = fileConfig.SigningOperators is { Length: > 0 }
                ? fileConfig.SigningOperators
                : preset.SigningOperators,
        };
    }

    private static SparkNetworkConfig? GetPreset(ChainName networkType)
    {
        if (networkType == NBitcoin.Bitcoin.Instance.Mainnet.ChainName) return SparkNetworkConfig.Mainnet;
        if (networkType == ChainName.Regtest) return SparkNetworkConfig.Regtest;
        // Spark itself supports only Mainnet + Regtest. Other BTCPay chains are unsupported.
        return null;
    }

    private sealed record FileConfig(
        string? SspUrl,
        string? SspIdentityPublicKeyHex,
        SigningOperatorConfig[]? SigningOperators);
}
