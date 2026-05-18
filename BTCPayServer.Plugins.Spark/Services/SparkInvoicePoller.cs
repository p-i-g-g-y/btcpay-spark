using BTCPayServer.Plugins.Spark.Configuration;
using BTCPayServer.Plugins.Spark.Lightning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NSpark.Services;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// Fallback path for Lightning RX completion: periodically polls
/// <c>wallet.GetLightningReceiveRequestStatusAsync(requestId)</c> for every <c>Pending</c>
/// invoice. Exponential backoff curve ported from piggy-backend's
/// <c>InvoiceMonitorService.ComputeBackoff</c>: 5 s for the first 10 attempts, then
/// exponential up to a 5-minute cap.
/// </summary>
public class SparkInvoicePoller(
    IInvoiceMappingService mapping,
    IStoreSparkWalletProvider walletProvider,
    BTCPayServer.BTCPayNetworkProvider networkProvider,
    IOptions<SparkPluginOptions> opts,
    ILogger<SparkInvoicePoller> logger) : BackgroundService
{
    private const string SettledStatus = "TRANSFER_COMPLETED";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = opts.Value;
        var network = networkProvider.BTC.NBitcoinNetwork;
        var tick = options.InvoicePollerTick;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(network, options, stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SparkInvoicePoller tick failed");
            }

            try { await Task.Delay(tick, stoppingToken); } catch { }
        }
    }

    private async Task ProcessOnceAsync(Network network, SparkPluginOptions options, CancellationToken ct)
    {
        var pending = await mapping.ListPendingForPollAsync(ct);
        if (pending.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var inv in pending)
        {
            if (inv.ExpiresAt <= now)
            {
                await mapping.MarkExpiredAsync(inv.RequestId, ct);
                continue;
            }

            var backoff = ComputeBackoff(options, inv.PollAttempt);
            if (inv.LastPolledAt + backoff > now)
                continue;

            try
            {
                var wallet = await walletProvider.GetByWalletIdAsync(inv.WalletId, ct);
                if (wallet is null)
                {
                    await mapping.RecordPollAttemptAsync(inv.RequestId, ct);
                    continue;
                }

                var status = await wallet.GetLightningReceiveRequestStatusAsync(inv.RequestId, ct);
                await mapping.RecordPollAttemptAsync(inv.RequestId, ct);

                if (string.Equals(status, SettledStatus, StringComparison.OrdinalIgnoreCase))
                {
                    if (await mapping.MarkPaidAsync(inv.RequestId, DateTimeOffset.UtcNow, ct))
                    {
                        var fresh = await mapping.GetByRequestIdAsync(inv.RequestId, ct);
                        if (fresh is not null)
                        {
                            var btcpay = SparkLightningInvoiceMapper.ToBtcPay(fresh, network);
                            mapping.Notify(inv.WalletId, btcpay);
                            logger.LogInformation("Spark Lightning invoice settled via poller (wallet={Wallet}, request={Request})",
                                inv.WalletId, inv.RequestId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Poll failed for invoice {RequestId}", inv.RequestId);
            }
        }
    }

    private static TimeSpan ComputeBackoff(SparkPluginOptions opts, int attempt)
    {
        if (attempt < opts.InvoiceFastPollAttempts) return opts.InvoicePollerTick;
        var baseMs = opts.InvoicePollerTick.TotalMilliseconds * 3; // start exponential at 3× fast tick
        var ms = baseMs * Math.Pow(2, attempt - opts.InvoiceFastPollAttempts);
        return TimeSpan.FromMilliseconds(Math.Min(opts.InvoiceMaxBackoff.TotalMilliseconds, ms));
    }
}
