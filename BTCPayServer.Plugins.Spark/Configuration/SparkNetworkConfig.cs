using NSpark;

namespace BTCPayServer.Plugins.Spark.Configuration;

/// <summary>
/// Plugin-level Spark network configuration. Mirrors the subset of <see cref="SparkOptions"/>
/// that we expose for override via <c>spark.json</c>; null fields fall back to NSpark's
/// per-network defaults (<see cref="SparkOptions.GetDefaultOperators"/> and
/// <see cref="SparkOptions.GetSspIdentityPublicKey"/>).
/// </summary>
public sealed record SparkNetworkConfig(
    SparkNetwork Network,
    string? SspUrl = null,
    string? SspIdentityPublicKeyHex = null,
    SigningOperatorConfig[]? SigningOperators = null)
{
    public static SparkNetworkConfig Mainnet { get; } = new(SparkNetwork.Mainnet);
    public static SparkNetworkConfig Regtest { get; } = new(SparkNetwork.Regtest);

    /// <summary>Apply this config to a <see cref="SparkOptions"/>, only overriding fields that are non-null.</summary>
    public void ApplyTo(SparkOptions options)
    {
        options.Network = Network;
        options.SigningOperators = SigningOperators ?? SparkOptions.GetDefaultOperators(Network);
        options.SspUrl = SspUrl ?? options.SspUrl;
        options.SspIdentityPublicKeyHex = SspIdentityPublicKeyHex ?? SparkOptions.GetSspIdentityPublicKey(Network);
    }
}
