using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Spark.Configuration;
using BTCPayServer.Plugins.Spark.Data;
using BTCPayServer.Plugins.Spark.Lightning;
using BTCPayServer.Plugins.Spark.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSpark;

namespace BTCPayServer.Plugins.Spark;

public class SparkPlugin : BaseBTCPayServerPlugin
{
    private const string DisableEnvVar = "BTCPAY_SPARK_DISABLED";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.4" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var pluginServices = (PluginServiceCollection)services;
        var configuration = pluginServices.BootstrapServices.GetRequiredService<IConfiguration>();

        if (string.Equals(Environment.GetEnvironmentVariable(DisableEnvVar), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var networkConfig = SparkConfigurationLoader.Load(configuration);
        if (networkConfig is null)
        {
            // Spark only supports Mainnet / Regtest; other BTCPay chains are unsupported.
            return;
        }

        RegisterDatabase(services);
        RegisterNSpark(services, networkConfig);
        RegisterPluginServices(services);
        RegisterLightning(services);
        RegisterHostedServices(services);
        RegisterUIExtensions(services);
    }

    private static void RegisterDatabase(IServiceCollection services)
    {
        services.AddSingleton<SparkPluginDbContextFactory>();
        services.AddDbContext<SparkPluginDbContext>((sp, o) =>
            sp.GetRequiredService<SparkPluginDbContextFactory>().ConfigureBuilder(o));
        services.AddDbContextFactory<SparkPluginDbContext>((sp, o) =>
            sp.GetRequiredService<SparkPluginDbContextFactory>().ConfigureBuilder(o));
        services.AddStartupTask<SparkPluginSchemaInitializer>();
    }

    private static void RegisterNSpark(IServiceCollection services, SparkNetworkConfig networkConfig)
    {
        services.AddSpark(opts => networkConfig.ApplyTo(opts));
        services.AddSingleton(networkConfig);
        services.AddOptions<SparkPluginOptions>();
    }

    private static void RegisterPluginServices(IServiceCollection services)
    {
        services.AddSingleton<IWalletSecretProtector, DataProtectorWalletSecretProtector>();
        services.AddSingleton<SparkWalletService>();
        services.AddSingleton<IStoreSparkWalletProvider, StoreSparkWalletProvider>();
        services.AddSingleton<IInvoiceMappingService, InvoiceMappingService>();
        services.AddSingleton<IOutgoingPaymentService, OutgoingPaymentService>();
        services.AddSingleton<ISparkTransferActivityService, SparkTransferActivityService>();
        services.AddSingleton<SparkDepositSignal>();
    }

    private static void RegisterLightning(IServiceCollection services)
    {
        services.AddSingleton<ILightningConnectionStringHandler, SparkLightningConnectionStringHandler>();
    }

    private static void RegisterHostedServices(IServiceCollection services)
    {
        services.AddSingleton<SparkEventSubscriber>();
        services.AddHostedService(sp => sp.GetRequiredService<SparkEventSubscriber>());

        services.AddSingleton<SparkInvoicePoller>();
        services.AddHostedService(sp => sp.GetRequiredService<SparkInvoicePoller>());

        services.AddSingleton<SparkOutgoingPaymentPoller>();
        services.AddHostedService(sp => sp.GetRequiredService<SparkOutgoingPaymentPoller>());

        services.AddSingleton<SparkDepositAutoClaimer>();
        services.AddHostedService(sp => sp.GetRequiredService<SparkDepositAutoClaimer>());
    }

    private static void RegisterUIExtensions(IServiceCollection services)
    {
        // Sidebar wallet entry visible on every store page.
        services.AddUIExtension("store-wallets-nav", "/Views/Spark/SparkWalletNav.cshtml");

        // Store dashboard widgets.
        services.AddUIExtension("dashboard", "/Views/Spark/SparkDashboardWidget.cshtml");
        services.AddUIExtension("dashboard", "/Views/Spark/SparkActivityDashboardWidget.cshtml");

        // Setup-guide hint shown when no Lightning method is configured.
        services.AddUIExtension("dashboard-setup-guide-payment", "/Views/Spark/DashboardSetupGuidePayment.cshtml");

        // "Use Spark" pill on the Lightning node setup page (next to "Use internal node" /
        // "Use custom node"). Clicking it hits SparkController.EnableLightning which sets the
        // store's Lightning connection string to type=spark;wallet-id={id}.
        services.AddUIExtension("ln-payment-method-setup-tabhead", "/Views/Spark/SparkLNSetupTabhead.cshtml");

        // Informational accordion item describing the Spark connection-string format. Injected
        // into BTCPay's CustomNodeSupport list at the bottom of the Custom-setup tab.
        services.AddUIExtension("ln-payment-method-setup-tab", "/Views/Lightning/LNPaymentMethodSetupTab.cshtml");
    }
}
