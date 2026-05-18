namespace BTCPayServer.Plugins.Spark.Exceptions;

public class SparkPluginException : Exception
{
    public SparkPluginException(string message) : base(message) { }
    public SparkPluginException(string message, Exception inner) : base(message, inner) { }
}

public class SparkWalletNotConfiguredException : SparkPluginException
{
    public SparkWalletNotConfiguredException(string storeId)
        : base($"No Spark wallet is configured for store {storeId}. Visit /plugins/spark/stores/{storeId}/initial-setup to configure one.") { }
}

public class SparkSendException : SparkPluginException
{
    public SparkSendException(string message) : base(message) { }
    public SparkSendException(string message, Exception inner) : base(message, inner) { }
}
