using BTCPayServer.Plugins.Spark.Configuration;
using BTCPayServer.Plugins.Spark.Data.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSpark.Services;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// Reconciliation loop for outgoing Lightning payments. After
/// <c>SparkLightningClient.Pay</c> submits a BOLT11 and persists a pending row, this poller
/// repeatedly calls NSpark's <c>GetLightningSendStatusAsync</c> for each pending row until the
/// SSP reports <c>SUCCEEDED</c> (record fee + preimage, mark Succeeded) or <c>FAILED</c>
/// (record error, mark Failed).
/// </summary>
/// <remarks>
/// Backoff curve is identical to <see cref="SparkInvoicePoller"/>: <c>InvoicePollerTick</c>
/// for the first N attempts (default 10), then exponential up to <c>InvoiceMaxBackoff</c>.
/// Past-expiry pending payments are NOT auto-failed — the SSP may settle the HTLC right at
/// the deadline. They are marked Failed only after a wide grace period
/// (<see cref="ExpiryGrace"/>) past the BOLT11 expiry.
/// </remarks>
public class SparkOutgoingPaymentPoller(
    IOutgoingPaymentService outgoingPayments,
    IStoreSparkWalletProvider walletProvider,
    IOptions<SparkPluginOptions> opts,
    ILogger<SparkOutgoingPaymentPoller> logger) : BackgroundService
{
    /// <summary>Wait this long past BOLT11 expiry before declaring a still-pending send as Failed.</summary>
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromMinutes(15);

    // Status values from Spark's LightningSendRequestStatus enum, partitioned by terminality.
    // PREIMAGE_PROVIDED is the earliest unambiguous "payment settled" — the sender has the
    // preimage and the destination is paid. LIGHTNING_PAYMENT_SUCCEEDED and TRANSFER_COMPLETED
    // are the same outcome from later vantage points in the SSP's state machine.
    private static readonly HashSet<string> SuccessStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PREIMAGE_PROVIDED",
        "LIGHTNING_PAYMENT_SUCCEEDED",
        "TRANSFER_COMPLETED",
    };

    private static readonly HashSet<string> FailureStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIGHTNING_PAYMENT_FAILED",
        "TRANSFER_FAILED",
        "PREIMAGE_PROVIDING_FAILED",
        "USER_TRANSFER_VALIDATION_FAILED",
        "USER_SWAP_RETURNED",       // sats returned to sender → from BTCPay's perspective, the payout failed
        "USER_SWAP_RETURN_FAILED",
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = opts.Value;
        var tick = options.InvoicePollerTick;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SparkOutgoingPaymentPoller tick failed");
            }

            try { await Task.Delay(tick, stoppingToken); } catch { }
        }
    }

    private async Task ProcessOnceAsync(SparkPluginOptions options, CancellationToken ct)
    {
        var pending = await outgoingPayments.ListPendingForPollAsync(ct);
        if (pending.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var p in pending)
        {
            var backoff = ComputeBackoff(options, p.PollAttempt);
            if (p.LastPolledAt + backoff > now) continue;

            try
            {
                // Without an SSP request id we can't query the user_request endpoint at all.
                // This happens when Pay() crashed between PayLightningInvoiceAsync returning and
                // AttachSspRequestIdAsync persisting — rare but possible. Wait it out; the BOLT11
                // expiry grace below will eventually fail the row.
                if (string.IsNullOrEmpty(p.SspRequestId))
                {
                    await outgoingPayments.RecordPollAttemptAsync(p.Id, ct);
                    await TryExpireAsync(p, now, ct);
                    continue;
                }

                var wallet = await walletProvider.GetByWalletIdAsync(p.WalletId, ct);
                if (wallet is null)
                {
                    await outgoingPayments.RecordPollAttemptAsync(p.Id, ct);
                    continue;
                }

                var status = await wallet.GetLightningSendStatusAsync(p.SspRequestId, ct);
                await outgoingPayments.RecordPollAttemptAsync(p.Id, ct);

                if (status is null)
                {
                    // SSP has no record of this request id. Most likely propagation delay
                    // immediately after submission; later it's a hard "lost" state. The expiry
                    // grace covers the latter — until BOLT11 expiry passes we keep retrying.
                    await TryExpireAsync(p, now, ct);
                    continue;
                }

                if (SuccessStatuses.Contains(status.Status))
                {
                    if (await outgoingPayments.MarkSucceededAsync(p.Id, status.Preimage, status.FeeSats, now, ct))
                    {
                        logger.LogInformation(
                            "Spark Lightning send succeeded (wallet={Wallet}, hash={Hash}, sspRequest={Request}, fee={Fee}, preimage={Preimage})",
                            p.WalletId, p.PaymentHash, p.SspRequestId, status.FeeSats, status.Preimage);
                    }
                    continue;
                }

                if (FailureStatuses.Contains(status.Status))
                {
                    if (await outgoingPayments.MarkFailedAsync(p.Id, $"SSP reported {status.Status}", now, ct))
                    {
                        logger.LogWarning(
                            "Spark Lightning send failed (wallet={Wallet}, hash={Hash}, sspRequest={Request}, sspStatus={Status})",
                            p.WalletId, p.PaymentHash, p.SspRequestId, status.Status);
                    }
                    continue;
                }

                // Still in flight (CREATED / REQUEST_VALIDATED / LIGHTNING_PAYMENT_INITIATED /
                // PENDING_USER_SWAP_RETURN / unknown future enum). Fall through to expiry-grace
                // check — the SSP can legitimately spend a long time in LIGHTNING_PAYMENT_INITIATED
                // while a payment is routing, so we don't fail proactively.
                await TryExpireAsync(p, now, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Outgoing send poll failed for hash {Hash} request {Request}",
                    p.PaymentHash, p.SspRequestId);
            }
        }
    }

    private async Task TryExpireAsync(SparkOutgoingPaymentEntity p, DateTimeOffset now, CancellationToken ct)
    {
        if (p.ExpiresAt is { } exp && exp + ExpiryGrace < now)
        {
            if (await outgoingPayments.MarkFailedAsync(p.Id, "BOLT11 expired with no SSP settlement.", now, ct))
            {
                logger.LogWarning(
                    "Spark Lightning send timed out past expiry (wallet={Wallet}, hash={Hash})",
                    p.WalletId, p.PaymentHash);
            }
        }
    }

    private static TimeSpan ComputeBackoff(SparkPluginOptions opts, int attempt)
    {
        if (attempt < opts.InvoiceFastPollAttempts) return opts.InvoicePollerTick;
        var baseMs = opts.InvoicePollerTick.TotalMilliseconds * 3;
        var ms = baseMs * Math.Pow(2, attempt - opts.InvoiceFastPollAttempts);
        return TimeSpan.FromMilliseconds(Math.Min(opts.InvoiceMaxBackoff.TotalMilliseconds, ms));
    }
}
