using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Spark.Configuration;
using BTCPayServer.Plugins.Spark.Data.Entities;
using BTCPayServer.Plugins.Spark.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NSpark;
using NSpark.Services;
using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.Spark.Lightning;

/// <summary>
/// BTCPay <see cref="IExtendedLightningClient"/> backed by an NSpark <see cref="SparkWallet"/>.
/// Owns no state itself — wallet lookup is cached by <see cref="IStoreSparkWalletProvider"/> and
/// invoice persistence lives in <see cref="IInvoiceMappingService"/>.
/// </summary>
public class SparkLightningClient(
    Network network,
    string walletId,
    IStoreSparkWalletProvider walletProvider,
    IInvoiceMappingService invoiceMapping,
    IOutgoingPaymentService outgoingPayments,
    IOptions<SparkPluginOptions> options,
    ILogger<SparkLightningClient> logger) : IExtendedLightningClient
{
    public string WalletId => walletId;

    public string DisplayName => "Spark";
    public Uri? ServerUri => null;
    public override string ToString() => $"type={SparkLightningConnectionStringHandler.ConnectionStringType};wallet-id={walletId}";

    public Task<ValidationResult?> Validate() => Task.FromResult<ValidationResult?>(System.ComponentModel.DataAnnotations.ValidationResult.Success);

    private async Task<SparkWallet> ResolveWalletAsync(CancellationToken ct)
    {
        var wallet = await walletProvider.GetByWalletIdAsync(walletId, ct)
                     ?? throw new InvalidOperationException($"Spark wallet '{walletId}' is not configured.");
        return wallet;
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation = default)
        => await CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams req, CancellationToken cancellation = default)
    {
        var wallet = await ResolveWalletAsync(cancellation);

        var amountSats = (long)req.Amount.ToUnit(LightMoneyUnit.Satoshi);
        if (amountSats <= 0)
            throw new InvalidOperationException("Invoice amount must be > 0 sats.");

        var expirySecs = req.Expiry.TotalSeconds > 0 ? (int)req.Expiry.TotalSeconds : (int?)null;

        var spark = await wallet.CreateLightningInvoiceAsync(
            amountSats,
            memo: req.Description,
            expirySecs: expirySecs,
            receiverIdentityPublicKey: null,
            descriptionHash: null,
            ct: cancellation);

        var entity = new SparkLightningInvoiceEntity
        {
            RequestId = spark.RequestId ?? spark.PaymentHash,
            WalletId = walletId,
            PaymentHash = spark.PaymentHash,
            Bolt11 = spark.PaymentRequest,
            AmountSats = spark.AmountSats,
            Memo = req.Description,
            Currency = "BTC",
            Status = SparkLightningInvoiceStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = spark.ExpiresAt,
            LastPolledAt = DateTimeOffset.UtcNow,
        };
        await invoiceMapping.UpsertAsync(entity, cancellation);

        logger.LogInformation(
            "Spark invoice created (wallet={WalletId}, request={RequestId}, hash={PaymentHash}, sats={Sats})",
            walletId, entity.RequestId, entity.PaymentHash, amountSats);

        return SparkLightningInvoiceMapper.ToBtcPay(entity, network);
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var entity = await invoiceMapping.GetByRequestIdAsync(invoiceId, cancellation);
        return entity is null ? null : SparkLightningInvoiceMapper.ToBtcPay(entity, network);
    }

    public async Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        var entity = await invoiceMapping.GetByPaymentHashAsync(walletId, paymentHash.ToString(), cancellation);
        return entity is null ? null : SparkLightningInvoiceMapper.ToBtcPay(entity, network);
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
        => await ListInvoices(new ListInvoicesParams(), cancellation);

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
    {
        var status = request.PendingOnly == true ? SparkLightningInvoiceStatus.Pending : (SparkLightningInvoiceStatus?)null;
        var entities = await invoiceMapping.ListAsync(walletId, status, includePast: true, take: 500, cancellation);
        return entities.Select(e => SparkLightningInvoiceMapper.ToBtcPay(e, network)).ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        var entity = await outgoingPayments.GetByPaymentHashAsync(walletId, paymentHash, cancellation);
        if (entity is null)
        {
            // BTCPay's LightningPendingPayoutListener treats Unknown / not-found the same way:
            // keep the payout in flight. Throwing here would mark the payout failed which is
            // worse than the truth (we genuinely don't know).
            return new LightningPayment
            {
                Id = paymentHash,
                PaymentHash = paymentHash,
                Status = LightningPaymentStatus.Unknown,
            };
        }
        return MapOutgoingPayment(entity);
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
        => await ListPayments(new ListPaymentsParams(), cancellation);

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
    {
        var rows = await outgoingPayments.ListAsync(walletId, take: 500, cancellation);
        return rows.Select(MapOutgoingPayment).ToArray();
    }

    private static LightningPayment MapOutgoingPayment(SparkOutgoingPaymentEntity e)
    {
        var status = e.Status switch
        {
            SparkOutgoingPaymentStatus.Succeeded => LightningPaymentStatus.Complete,
            SparkOutgoingPaymentStatus.Failed => LightningPaymentStatus.Failed,
            _ => LightningPaymentStatus.Pending,
        };
        return new LightningPayment
        {
            Id = e.Id,
            PaymentHash = e.PaymentHash,
            BOLT11 = e.Bolt11,
            Preimage = e.Preimage,
            Status = status,
            Amount = e.AmountSats is { } a ? LightMoney.Satoshis(a) : null,
            AmountSent = e.AmountSats is { } a2
                ? LightMoney.Satoshis(a2 + (e.ActualFeeSats ?? 0))
                : null,
            Fee = e.ActualFeeSats is { } f ? LightMoney.Satoshis(f) : null,
            CreatedAt = e.CreatedAt,
        };
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var wallet = await ResolveWalletAsync(cancellation);
        var balance = await wallet.GetBalanceAsync(cancellation);
        return new LightningNodeBalance
        {
            OffchainBalance = new OffchainBalance
            {
                Local = LightMoney.Satoshis(balance.SatsBalance.Available),
            }
        };
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
        => Task.FromResult<ILightningInvoiceListener>(
            new SparkLightningInvoiceListener(walletId, invoiceMapping, logger));

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
        => Pay(bolt11, new PayInvoiceParams(), cancellation);

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
        => throw new NotSupportedException("BOLT11 is required.");

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(bolt11))
            return new PayResponse(PayResult.Error, "BOLT11 is required.");

        BOLT11PaymentRequest pr;
        try
        {
            pr = BOLT11PaymentRequest.Parse(bolt11, network);
        }
        catch (Exception ex)
        {
            return new PayResponse(PayResult.Error, $"Invalid BOLT11: {ex.Message}");
        }

        var paymentHash = pr.PaymentHash?.ToString();
        if (string.IsNullOrEmpty(paymentHash))
            return new PayResponse(PayResult.Error, "BOLT11 has no payment hash.");

        var amountSats = pr.MinimumAmount is null
            ? (long?)null
            : (long)pr.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);

        // Resolve max-fee cap. BTCPay's payout processor sets MaxFeePercent (not MaxFeeFlat) —
        // honor both, picking the lower bound when both are present.
        long? maxFeeSats = null;
        if (payParams.MaxFeeFlat is { } flat)
            maxFeeSats = (long)flat.ToUnit(MoneyUnit.Satoshi);
        if (payParams.MaxFeePercent is { } pct && amountSats is { } amt && amt > 0)
        {
            var pctSats = (long)Math.Ceiling((double)amt * (double)pct / 100.0);
            maxFeeSats = maxFeeSats is { } existing ? Math.Min(existing, pctSats) : pctSats;
        }

        // Idempotent persistence — if this BOLT11 has been seen before, do not re-send.
        var pending = new SparkOutgoingPaymentEntity
        {
            WalletId = walletId,
            PaymentHash = paymentHash,
            Bolt11 = bolt11,
            AmountSats = amountSats,
            MaxFeeSats = maxFeeSats,
            Status = SparkOutgoingPaymentStatus.Pending,
            ExpiresAt = pr.ExpiryDate,
            Memo = pr.ShortDescription,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastPolledAt = DateTimeOffset.UtcNow,
        };
        var (entity, inserted) = await outgoingPayments.EnsurePendingAsync(pending, cancellation);

        if (!inserted)
        {
            // Already known — map current DB state into a PayResponse without contacting the SSP.
            return entity.Status switch
            {
                SparkOutgoingPaymentStatus.Succeeded => new PayResponse
                {
                    Result = PayResult.Ok,
                    Details = new PayDetails
                    {
                        PaymentHash = pr.PaymentHash,
                        Preimage = entity.Preimage is null ? null : uint256.Parse(entity.Preimage),
                        Status = LightningPaymentStatus.Complete,
                        TotalAmount = amountSats is { } a3 ? LightMoney.Satoshis(a3) : null,
                        FeeAmount = entity.ActualFeeSats is { } af ? LightMoney.Satoshis(af) : null,
                    },
                },
                SparkOutgoingPaymentStatus.Failed => new PayResponse(
                    PayResult.Error,
                    entity.LastError ?? "Previously failed."),
                _ => new PayResponse
                {
                    Result = PayResult.Unknown,
                    Details = new PayDetails
                    {
                        PaymentHash = pr.PaymentHash,
                        Status = LightningPaymentStatus.Pending,
                    },
                },
            };
        }

        // Fresh record — talk to the SSP.
        SparkWallet wallet;
        try
        {
            wallet = await ResolveWalletAsync(cancellation);
        }
        catch (Exception ex)
        {
            await outgoingPayments.MarkFailedAsync(entity.Id, ex.Message, DateTimeOffset.UtcNow, cancellation);
            return new PayResponse(PayResult.Error, ex.Message);
        }

        string sspRequestId;
        try
        {
            sspRequestId = await wallet.PayLightningInvoiceAsync(bolt11, maxFeeSats, cancellation);
            await outgoingPayments.AttachSspRequestIdAsync(entity.Id, sspRequestId, cancellation);

            logger.LogInformation(
                "Spark Lightning send submitted (wallet={WalletId}, paymentHash={PaymentHash}, sspRequestId={SspRequestId})",
                walletId, paymentHash, sspRequestId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Spark Lightning pay failed for wallet {WalletId} hash {PaymentHash}", walletId, paymentHash);
            await outgoingPayments.MarkFailedAsync(entity.Id, ex.Message, DateTimeOffset.UtcNow, cancellation);
            return new PayResponse(PayResult.Error, ex.Message);
        }

        // Block briefly waiting for a terminal status. Spark mainnet sends typically settle in
        // 2–5 s; LND/c-lightning's Pay() also blocks until terminal, so BTCPay's payout pipeline
        // expects synchronous resolution and only falls back to InProgress + 10-minute reconcile
        // when Pay() returns Unknown. We mirror that behaviour here for good UX, but cap the wait
        // at OutgoingPaymentWaitForTerminal so a stuck route doesn't hang the request thread.
        return await WaitForTerminalAsync(wallet, entity.Id, sspRequestId, pr, amountSats, cancellation);
    }

    /// <summary>
    /// Polls the SSP send status for up to <see cref="SparkPluginOptions.OutgoingPaymentWaitForTerminal"/>
    /// and writes any terminal state to the DB. Returns a <see cref="PayResponse"/> reflecting the
    /// final observed state — <c>Ok</c>+<c>Complete</c> if settled (preimage attached so BTCPay's
    /// payout processor stores it on the payout proof), <c>Error</c> if SSP reported a hard failure,
    /// or <c>Unknown</c>+<c>Pending</c> if we hit the timeout (the background poller will reconcile).
    /// </summary>
    private async Task<PayResponse> WaitForTerminalAsync(
        SparkWallet wallet,
        string entityId,
        string sspRequestId,
        BOLT11PaymentRequest pr,
        long? amountSats,
        CancellationToken cancellation)
    {
        var opts = options.Value;
        var deadline = DateTimeOffset.UtcNow + opts.OutgoingPaymentWaitForTerminal;
        var pollInterval = opts.OutgoingPaymentWaitPollInterval;

        while (DateTimeOffset.UtcNow < deadline && !cancellation.IsCancellationRequested)
        {
            NSpark.Models.LightningSendStatus? status;
            try
            {
                status = await wallet.GetLightningSendStatusAsync(sspRequestId, cancellation);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Transient SSP error — back off and retry. Don't mark the row Failed: the send
                // already left the wallet, so the background poller must keep watching.
                logger.LogDebug(ex, "Status poll inside Pay() failed for {SspRequestId}", sspRequestId);
                status = null;
            }

            if (status is not null)
            {
                if (IsTerminalSuccess(status.Status))
                {
                    // SECURITY: validate the SSP-reported preimage against the BOLT11's payment
                    // hash before accepting "Succeeded" as terminal. A malicious / buggy SSP
                    // returning junk would otherwise lock the row Succeeded with no real proof
                    // of payment. If validation fails, stay in the poll loop — the SSP might
                    // return a correct preimage on a later tick, or the bg poller will reconcile.
                    var paymentHashHex = pr.PaymentHash?.ToString();
                    if (!SparkOutgoingPaymentPoller.IsValidPreimage(status.Preimage, paymentHashHex))
                    {
                        logger.LogError(
                            "Spark Lightning send reported {Status} inside Pay() but preimage does not match payment hash (wallet={WalletId}, sspRequestId={SspRequestId}). Will keep polling.",
                            status.Status, walletId, sspRequestId);
                    }
                    else
                    {
                        await outgoingPayments.MarkSucceededAsync(entityId, status.Preimage, status.FeeSats, DateTimeOffset.UtcNow, cancellation);
                        logger.LogInformation(
                            "Spark Lightning send settled inside Pay() (wallet={WalletId}, sspRequestId={SspRequestId}, status={Status})",
                            walletId, sspRequestId, status.Status);
                        return new PayResponse
                        {
                            Result = PayResult.Ok,
                            Details = new PayDetails
                            {
                                PaymentHash = pr.PaymentHash,
                                Preimage = uint256.Parse(status.Preimage!),
                                Status = LightningPaymentStatus.Complete,
                                TotalAmount = amountSats is { } a3 ? LightMoney.Satoshis(a3) : null,
                                FeeAmount = status.FeeSats is { } f ? LightMoney.Satoshis(f) : null,
                            },
                        };
                    }
                }

                if (IsTerminalFailure(status.Status))
                {
                    var errMsg = $"SSP reported {status.Status}";
                    await outgoingPayments.MarkFailedAsync(entityId, errMsg, DateTimeOffset.UtcNow, cancellation);
                    logger.LogWarning(
                        "Spark Lightning send failed inside Pay() (wallet={WalletId}, sspRequestId={SspRequestId}, status={Status})",
                        walletId, sspRequestId, status.Status);
                    return new PayResponse(PayResult.Error, errMsg);
                }
                // else: still in flight — keep polling.
            }

            try { await Task.Delay(pollInterval, cancellation); }
            catch (OperationCanceledException) { throw; }
        }

        // Timed out waiting. SSP accepted the request, so the send is in flight — leave the
        // payout in BTCPay's InProgress bucket and let the background poller finish reconciling.
        return new PayResponse
        {
            Result = PayResult.Unknown,
            Details = new PayDetails
            {
                PaymentHash = pr.PaymentHash,
                Status = LightningPaymentStatus.Pending,
                TotalAmount = amountSats is { } a4 ? LightMoney.Satoshis(a4) : null,
            },
        };
    }

    // Spark's LightningSendRequestStatus enum — duplicated from SparkOutgoingPaymentPoller so the
    // sync path has the same definition of "done". Kept in lock-step manually; if these drift,
    // the worst case is the in-Pay() wait misses a terminal status and falls back to Unknown,
    // and the background poller catches it 10 minutes later.
    private static bool IsTerminalSuccess(string s) =>
        s.Equals("PREIMAGE_PROVIDED", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("LIGHTNING_PAYMENT_SUCCEEDED", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("TRANSFER_COMPLETED", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalFailure(string s) =>
        s.Equals("LIGHTNING_PAYMENT_FAILED", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("TRANSFER_FAILED", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("PREIMAGE_PROVIDING_FAILED", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("USER_TRANSFER_VALIDATION_FAILED", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("USER_SWAP_RETURNED", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("USER_SWAP_RETURN_FAILED", StringComparison.OrdinalIgnoreCase);

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
        => throw new NotSupportedException("Use SparkController.Receive for on-chain deposit addresses.");

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
        => Task.FromResult(Array.Empty<LightningChannel>());
}
