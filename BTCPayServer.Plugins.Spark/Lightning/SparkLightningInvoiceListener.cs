using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Spark.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Spark.Lightning;

/// <summary>
/// BTCPay-side listener that waits on the <see cref="IInvoiceMappingService"/>'s per-wallet channel
/// for paid invoice notifications. The fan-in side (event subscriber + poller) calls
/// <see cref="IInvoiceMappingService.Notify"/> with the mapped <see cref="LightningInvoice"/>.
/// </summary>
public class SparkLightningInvoiceListener : ILightningInvoiceListener
{
    private readonly System.Threading.Channels.ChannelReader<LightningInvoice> _reader;
    private readonly IDisposable _lease;
    private readonly ILogger _logger;
    private readonly string _walletId;
    private int _disposed;

    public SparkLightningInvoiceListener(string walletId, IInvoiceMappingService mapping, ILogger logger)
    {
        _walletId = walletId;
        _logger = logger;
        _reader = mapping.AcquireListener(walletId, out _lease);
    }

    public async Task<LightningInvoice?> WaitInvoice(CancellationToken cancellation)
    {
        try
        {
            while (await _reader.WaitToReadAsync(cancellation))
            {
                if (_reader.TryRead(out var invoice))
                    return invoice;
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spark listener error for wallet {WalletId}", _walletId);
        }
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lease.Dispose();
    }
}
