namespace BTCPayServer.Plugins.Spark.Models;

public class ServiceConnectionStatus
{
    public string Name { get; set; } = "";
    public string? Endpoint { get; set; }
    public bool Connected { get; set; }
    public string? Error { get; set; }
}
