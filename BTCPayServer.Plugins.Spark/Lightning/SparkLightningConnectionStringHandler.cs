using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.Spark.Lightning;

/// <summary>
/// Parses BTCPay Lightning connection strings of the form
/// <c>type=spark;wallet-id={id}</c> and instantiates a <see cref="SparkLightningClient"/>.
/// </summary>
public class SparkLightningConnectionStringHandler(IServiceProvider serviceProvider) : ILightningConnectionStringHandler
{
    public const string ConnectionStringType = "spark";

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (!string.Equals(type, ConnectionStringType, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The key 'type' must be set to '{ConnectionStringType}' for Spark connection strings";
            return null;
        }

        if (!kv.TryGetValue("wallet-id", out var walletId) || string.IsNullOrWhiteSpace(walletId))
        {
            error = "The key 'wallet-id' is mandatory for Spark connection strings";
            return null;
        }

        error = null;
        return ActivatorUtilities.CreateInstance<SparkLightningClient>(serviceProvider, network, walletId);
    }
}
