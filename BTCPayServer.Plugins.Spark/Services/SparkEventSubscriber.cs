using System.Collections.Concurrent;
using BTCPayServer.Plugins.Spark.Data.Entities;
using BTCPayServer.Plugins.Spark.Lightning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NSpark;
using NSpark.Models;
using NSpark.Services;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// Maintains one <c>SubscribeEventsAsync</c> loop per ready wallet. Routes:
/// <list type="bullet">
/// <item><see cref="ConnectedEvent"/> → runs <c>ClaimPendingTransfersAsync</c> as a catch-up sweep
/// for anything that arrived while we were offline.</item>
/// <item><see cref="TransferReceivedEvent"/> → immediately calls <c>ClaimPendingTransfersAsync</c>
/// (materialises the new Spark transfer as a spendable leaf), then tries to fast-path-settle any
/// matching pending Lightning invoice so the customer-facing BTCPay invoice flips to Paid without
/// waiting for the 5 s poller tick.</item>
/// <item><see cref="DepositConfirmedEvent"/> → signals <see cref="SparkDepositAutoClaimer"/>.</item>
/// </list>
/// Recovers from <see cref="SparkConnectionException"/> with exponential backoff (cap 60 s).
/// </summary>
public class SparkEventSubscriber(
    IStoreSparkWalletProvider walletProvider,
    IInvoiceMappingService invoiceMapping,
    BTCPayServer.BTCPayNetworkProvider networkProvider,
    SparkDepositSignal depositSignal,
    ILogger<SparkEventSubscriber> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        walletProvider.WalletReady += OnWalletReady;
        walletProvider.WalletEvicted += OnWalletEvicted;

        try
        {
            // Start subscriptions for any wallets that already exist on boot.
            var existing = await walletProvider.ReadyWalletsAsync(stoppingToken);
            foreach (var entity in existing)
            {
                _ = StartLoopAsync(entity, stoppingToken);
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            walletProvider.WalletReady -= OnWalletReady;
            walletProvider.WalletEvicted -= OnWalletEvicted;
            foreach (var cts in _running.Values) cts.Cancel();
        }
    }

    private void OnWalletReady(object? sender, SparkWalletEntity entity)
    {
        _ = StartLoopAsync(entity, CancellationToken.None);
    }

    private void OnWalletEvicted(object? sender, string walletId)
    {
        if (_running.TryRemove(walletId, out var cts))
            cts.Cancel();
    }

    private async Task StartLoopAsync(SparkWalletEntity entity, CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        if (!_running.TryAdd(entity.Id, cts))
        {
            cts.Dispose();
            return;
        }

        var network = networkProvider.BTC.NBitcoinNetwork;
        var attempt = 0;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var wallet = await walletProvider.GetByWalletIdAsync(entity.Id, cts.Token);
                if (wallet is null) break;

                logger.LogInformation("Spark event subscription starting (wallet={WalletId})", entity.Id);
                await foreach (var evt in wallet.SubscribeEventsAsync(cts.Token))
                {
                    attempt = 0;
                    await DispatchAsync(wallet, entity, evt, network, cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(60_000, 1000 * Math.Pow(2, attempt)));
                logger.LogWarning(ex,
                    "Spark event subscription error (wallet={WalletId}, attempt={Attempt}); retrying in {DelayMs}ms",
                    entity.Id, attempt, (int)delay.TotalMilliseconds);
                try { await Task.Delay(delay, cts.Token); } catch { }
            }
        }

        _running.TryRemove(entity.Id, out _);
        cts.Dispose();
    }

    private async Task DispatchAsync(SparkWallet wallet, SparkWalletEntity entity, SparkEvent evt, Network network, CancellationToken ct)
    {
        switch (evt)
        {
            case ConnectedEvent:
                logger.LogInformation("Spark event stream connected (wallet={WalletId})", entity.Id);
                try
                {
                    // Catch-up: claim any pending Spark-to-Spark transfers that arrived while we were offline.
                    var claimed = await wallet.ClaimPendingTransfersAsync(ct);
                    if (claimed.Count > 0)
                        logger.LogInformation("Claimed {Count} pending transfers for wallet {WalletId}", claimed.Count, entity.Id);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "ClaimPendingTransfersAsync catch-up failed");
                }
                break;

            case TransferReceivedEvent transfer:
                await HandleTransferReceivedAsync(transfer.Transfer, entity, wallet, network, ct);
                break;

            case DepositConfirmedEvent dep:
                logger.LogInformation("Deposit confirmed event (wallet={WalletId}, treeId={TreeId})", entity.Id, dep.TreeId);
                depositSignal.Signal(entity.Id);
                break;
        }
    }

    /// <summary>
    /// A <see cref="TransferReceivedEvent"/> means a Spark-side transfer (Spark-to-Spark, the
    /// transfer-leg of a Lightning receive, or the credit from a static-deposit claim) is ready
    /// to be claimed. Two-step handling:
    /// <list type="number">
    /// <item><c>ClaimPendingTransfersAsync</c> — materialise the transfer as spendable leaves.
    /// Idempotent: deposits/Lightning paths also call this from their own loops.</item>
    /// <item>Fast-path Lightning settlement — if any pending Lightning invoice matches this
    /// transfer's amount, confirm via the SSP status query and notify the BTCPay listener channel
    /// immediately. The poller would pick this up within 5 s anyway; this just avoids the
    /// up-to-5-s latency for the customer.</item>
    /// </list>
    /// </summary>
    private async Task HandleTransferReceivedAsync(SparkTransfer transfer, SparkWalletEntity entity, SparkWallet wallet, Network network, CancellationToken ct)
    {
        logger.LogInformation(
            "Transfer received (wallet={WalletId}, transferId={TransferId}, sats={Sats}, type={Type}, status={Status})",
            entity.Id, transfer.Id, transfer.TotalValueSats, transfer.Type, transfer.Status);

        try
        {
            var claimed = await wallet.ClaimPendingTransfersAsync(ct);
            if (claimed.Count > 0)
                logger.LogInformation("Claimed {Count} pending transfer(s) for wallet {WalletId}",
                    claimed.Count, entity.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ClaimPendingTransfersAsync failed for wallet {WalletId} after TransferReceivedEvent", entity.Id);
        }

        // Fast-path: if this transfer matches a pending Lightning invoice by amount, confirm
        // settlement with the SSP and notify the BTCPay listener immediately.
        try
        {
            var pending = await invoiceMapping.ListAsync(entity.Id, Data.Entities.SparkLightningInvoiceStatus.Pending,
                includePast: false, take: 50, ct);
            foreach (var inv in pending.Where(i => i.AmountSats == transfer.TotalValueSats))
            {
                var status = await wallet.GetLightningReceiveRequestStatusAsync(inv.RequestId, ct);
                if (!string.Equals(status, "TRANSFER_COMPLETED", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!await invoiceMapping.MarkPaidAsync(inv.RequestId, DateTimeOffset.UtcNow, ct))
                    continue;
                var fresh = await invoiceMapping.GetByRequestIdAsync(inv.RequestId, ct);
                if (fresh is null) continue;
                var btcpay = SparkLightningInvoiceMapper.ToBtcPay(fresh, network);
                invoiceMapping.Notify(entity.Id, btcpay);
                logger.LogInformation(
                    "Lightning invoice settled via TransferReceivedEvent (wallet={Wallet}, request={Request})",
                    entity.Id, inv.RequestId);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Event-driven Lightning invoice correlation failed (wallet={WalletId})", entity.Id);
        }
    }
}

/// <summary>Cross-service hint mechanism: event subscriber signals → deposit auto-claimer wakes immediately.</summary>
public class SparkDepositSignal
{
    private readonly System.Threading.Channels.Channel<string> _channel
        = System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

    public void Signal(string walletId) => _channel.Writer.TryWrite(walletId);
    public System.Threading.Channels.ChannelReader<string> Reader => _channel.Reader;
}
