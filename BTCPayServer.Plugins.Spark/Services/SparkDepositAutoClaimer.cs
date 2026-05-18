using System.Net.Http.Json;
using System.Text.Json;
using BTCPayServer.Plugins.Spark.Configuration;
using BTCPayServer.Plugins.Spark.Data;
using BTCPayServer.Plugins.Spark.Data.Entities;
using BTCPayServer.Plugins.Spark.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NSpark;
using NSpark.Services;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// Discovers on-chain UTXOs at each wallet's static deposit address and walks them through the
/// two-step Spark claim state machine documented in nspark/docs/deposits.md:
///
/// <list type="number">
///   <item>
///     <description>
///     <c>GetUtxosForDepositAddressAsync(staticAddress, excludeClaimed: true)</c> — list new UTXOs
///     and persist each as <see cref="SparkWalletDepositStatus.Discovered"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>ClaimStaticDepositAsync(txid, vout)</c> — co-sign the SSP quote; returns a transfer id.
///     Row moves to <see cref="SparkWalletDepositStatus.ClaimedToTransfer"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>ClaimPendingTransfersAsync()</c> — materialize the pending transfer(s) as spendable leaves.
///     Row moves to <see cref="SparkWalletDepositStatus.Settled"/>.
///     </description>
///   </item>
/// </list>
///
/// Runs on a periodic timer and on an immediate signal from <see cref="SparkEventSubscriber"/>
/// (<c>DepositConfirmedEvent</c>).
/// </summary>
public class SparkDepositAutoClaimer(
    IDbContextFactory<SparkPluginDbContext> dbContextFactory,
    IStoreSparkWalletProvider walletProvider,
    SparkWalletService walletService,
    SparkDepositSignal signal,
    SparkConnection sparkConnection,
    IHttpClientFactory httpClientFactory,
    IOptions<SparkPluginOptions> opts,
    ILogger<SparkDepositAutoClaimer> logger) : BackgroundService
{
    // No per-deposit backoff: the outer sweep already gates retry frequency
    // (SparkPluginOptions.DepositSweepInterval, default 60 s). Every Discovered deposit gets a
    // claim attempt on every sweep until it reaches ClaimedToTransfer.

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweepEvery = opts.Value.DepositSweepInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAllAsync(stoppingToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "SparkDepositAutoClaimer sweep failed"); }

            // Wait either for the periodic timer or for a "deposit confirmed" signal from the
            // event subscriber, whichever fires first.
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delayTask = Task.Delay(sweepEvery, delayCts.Token);
            var signalTask = signal.Reader.WaitToReadAsync(delayCts.Token).AsTask();
            await Task.WhenAny(delayTask, signalTask);
            delayCts.Cancel();
            while (signal.Reader.TryRead(out _)) { } // drain
        }
    }

    private async Task SweepAllAsync(CancellationToken ct)
    {
        var wallets = await walletProvider.ReadyWalletsAsync(ct);
        foreach (var w in wallets)
            await SweepWalletAsync(w, ct);
    }

    private async Task SweepWalletAsync(SparkWalletEntity entity, CancellationToken ct)
    {
        var wallet = await walletProvider.GetByWalletIdAsync(entity.Id, ct);
        if (wallet is null) return;

        // Step 0: ensure we have a static deposit address. The plugin uses ONLY this address —
        // never single-use addresses. nspark guarantees it's deterministic, so calling repeatedly
        // is cheap and yields the same address.
        var staticAddr = entity.StaticDepositAddress;
        if (string.IsNullOrEmpty(staticAddr))
        {
            try
            {
                var sda = await wallet.GetStaticDepositAddressAsync(ct);
                staticAddr = sda.Address;
                await walletService.SetStaticDepositAddressAsync(entity.Id, staticAddr, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "GetStaticDepositAddressAsync failed for wallet {WalletId}", entity.Id);
                return;
            }
        }

        // Step 1: discover new UTXOs at the static address.
        try
        {
            var utxos = await wallet.GetUtxosForDepositAddressAsync(staticAddr, excludeClaimed: true, ct);
            if (utxos is { Count: > 0 })
                await PersistDiscoveredAsync(entity, staticAddr, utxos, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GetUtxosForDepositAddressAsync failed for {Address}", staticAddr);
        }

        // Step 2: ClaimStaticDepositAsync for every Discovered row whose backoff is due.
        //
        // Retries are unbounded — the auto-claimer keeps trying forever with exponential backoff.
        // One automatic claim attempt per deposit: if it succeeds → ClaimedToTransfer; if it
        // fails for ANY reason (SSP error, max-fee rejection, mempool unreachable, …) we mark
        // the deposit Failed and the auto-claimer leaves it alone from then on. The admin
        // surfaces the error in History and clicks "Retry now" to push it back to Discovered
        // for a fresh attempt. This keeps us from hammering the SSP on permanent failures and
        // makes failure modes visible by default.
        await using (var dbctx = await dbContextFactory.CreateDbContextAsync(ct))
        {
            var discovered = await dbctx.WalletDeposits
                .Where(d => d.WalletId == entity.Id
                    && d.Status == SparkWalletDepositStatus.Discovered)
                .ToListAsync(ct);

            foreach (var deposit in discovered)
            {
                try
                {
                    // claimStaticDepositWithMaxFee-equivalent client-side guard (mirrors JS SDK):
                    //   1. Get SSP credit quote.
                    //   2. Fetch UTXO amount from mempool.space (NSpark uses the same source
                    //      internally for FetchRawTransactionAsync).
                    //   3. fee = utxo - credit; refuse to claim if fee > MaxClaimFeeSats.
                    //   4. Otherwise call ClaimStaticDepositAsync — which internally re-fetches
                    //      a fresh quote and submits a FIXED_AMOUNT claim.
                    var quote = await wallet.GetDepositFeeEstimateAsync(deposit.TxId, (uint)deposit.Vout, ct);
                    var utxoSats = await FetchUtxoAmountSatsAsync(deposit.TxId, (uint)deposit.Vout, ct);
                    var feeSats = utxoSats - quote.CreditAmountSats;
                    var maxFeeSats = opts.Value.MaxClaimFeeSats;
                    deposit.AmountSats = quote.CreditAmountSats; // persist for UI visibility

                    if (feeSats > maxFeeSats)
                    {
                        deposit.ClaimAttempt++;
                        deposit.Status = SparkWalletDepositStatus.Failed;
                        deposit.LastError =
                            $"SSP fee {feeSats} sats exceeds max {maxFeeSats} sats (UTXO {utxoSats} → credit {quote.CreditAmountSats}). " +
                            "Raise SparkPluginOptions.MaxClaimFeeSats and click Retry, or refund this deposit via RefundStaticDepositAsync.";
                        logger.LogWarning(
                            "Claim rejected by max-fee guard (wallet={WalletId}, {TxId}:{Vout}, utxo={Utxo} credit={Credit} fee={Fee} > max={Max})",
                            entity.Id, deposit.TxId, deposit.Vout, utxoSats, quote.CreditAmountSats, feeSats, maxFeeSats);
                        continue;
                    }

                    var transferId = await wallet.ClaimStaticDepositAsync(deposit.TxId, (uint)deposit.Vout, ct);
                    deposit.TransferId = transferId;
                    deposit.Status = SparkWalletDepositStatus.ClaimedToTransfer;
                    deposit.ClaimedAt = DateTimeOffset.UtcNow;
                    deposit.ClaimAttempt++;
                    deposit.LastError = null;
                    logger.LogInformation(
                        "ClaimStaticDepositAsync ok (wallet={WalletId}, {TxId}:{Vout}, credit={Credit} fee={Fee}) → transfer {TransferId} after {Attempts} attempt(s)",
                        entity.Id, deposit.TxId, deposit.Vout, quote.CreditAmountSats, feeSats, transferId, deposit.ClaimAttempt);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    deposit.ClaimAttempt++;
                    deposit.Status = SparkWalletDepositStatus.Failed;
                    deposit.LastError = ex.Message;
                    logger.LogWarning(ex,
                        "Claim failed (wallet={WalletId}, deposit={Id}, attempt={Attempt}); marked Failed — admin can review and click Retry in History",
                        entity.Id, deposit.Id, deposit.ClaimAttempt);
                }
            }
            if (discovered.Count > 0)
                await dbctx.SaveChangesAsync(ct);
        }

        // Step 3: ClaimPendingTransfersAsync — materialises everything that step 2 produced (and
        // any inbound Spark transfers received while we were offline). Called once per sweep
        // because it's wallet-scoped, not per-deposit.
        IReadOnlyList<NSpark.Models.SparkTransfer> claimedTransfers;
        try
        {
            claimedTransfers = await wallet.ClaimPendingTransfersAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ClaimPendingTransfersAsync failed for wallet {WalletId}", entity.Id);
            return;
        }
        if (claimedTransfers.Count == 0) return;

        logger.LogInformation(
            "ClaimPendingTransfersAsync ok (wallet={WalletId}, claimed={Count})",
            entity.Id, claimedTransfers.Count);

        // Mark deposit rows as Settled when their TransferId appears among the claimed transfers.
        await using (var dbctx = await dbContextFactory.CreateDbContextAsync(ct))
        {
            var byTransferId = claimedTransfers.ToDictionary(t => t.Id);
            var pending = await dbctx.WalletDeposits
                .Where(d => d.WalletId == entity.Id
                    && d.Status == SparkWalletDepositStatus.ClaimedToTransfer
                    && d.TransferId != null)
                .ToListAsync(ct);
            foreach (var deposit in pending)
            {
                if (deposit.TransferId is not null && byTransferId.TryGetValue(deposit.TransferId, out var transfer))
                {
                    deposit.Status = SparkWalletDepositStatus.Settled;
                    deposit.SettledAt = DateTimeOffset.UtcNow;
                    if (deposit.AmountSats is null) deposit.AmountSats = transfer.TotalValueSats;
                }
            }
            await dbctx.SaveChangesAsync(ct);
        }
    }

    private async Task PersistDiscoveredAsync(SparkWalletEntity entity, string address, IReadOnlyList<NSpark.Models.DepositUtxo> utxos, CancellationToken ct)
    {
        await using var dbctx = await dbContextFactory.CreateDbContextAsync(ct);
        var existingKeys = await dbctx.WalletDeposits
            .Where(d => d.WalletId == entity.Id && d.Address == address)
            .Select(d => d.TxId + ":" + d.Vout)
            .ToListAsync(ct);
        var existingSet = existingKeys.ToHashSet();

        foreach (var u in utxos)
        {
            // NSpark's GetUtxosForDepositAddressAsync returns the txid in internal byte order
            // (the byte-reverse of the form mempool.space / RPCs / users use). Reverse once on
            // ingest so everything downstream — DB rows, History display, mempool.space URLs,
            // and the ClaimStaticDepositAsync / GetDepositFeeEstimateAsync calls that route
            // through the SSP's GraphQL transaction_id field — sees canonical display-order hex.
            var displayTxid = SparkLinkHelper.ReverseHex(u.Txid);
            var key = $"{displayTxid}:{u.Vout}";
            if (existingSet.Contains(key)) continue;
            dbctx.WalletDeposits.Add(new SparkWalletDepositEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                WalletId = entity.Id,
                Address = address,
                TxId = displayTxid,
                Vout = (int)u.Vout,
                Status = SparkWalletDepositStatus.Discovered,
                SeenAt = DateTimeOffset.UtcNow,
            });
            logger.LogInformation(
                "Discovered new static-deposit UTXO (wallet={WalletId}, {TxId}:{Vout})",
                entity.Id, displayTxid, u.Vout);
        }
        await dbctx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fetch the satoshi value of a specific output from a Bitcoin transaction. Mirrors how
    /// NSpark internally queries mempool.space in <c>FetchRawTransactionAsync</c>, but uses the
    /// JSON endpoint so we don't have to parse raw transaction bytes ourselves.
    /// On regtest the call may fail (no public explorer); we surface the error to caller so the
    /// max-fee guard treats unknown UTXO amounts as "skip claim until admin reviews".
    /// </summary>
    private async Task<long> FetchUtxoAmountSatsAsync(string txid, uint vout, CancellationToken ct)
    {
        var baseUrl = sparkConnection.Options.Network == SparkNetwork.Mainnet
            ? "https://mempool.space/api"
            : "http://localhost:3000"; // regtest electrs default; same convention NSpark uses
        var url = $"{baseUrl}/tx/{txid}";

        using var http = httpClientFactory.CreateClient(nameof(SparkDepositAutoClaimer));
        http.Timeout = TimeSpan.FromSeconds(10);
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("vout", out var outputs) || outputs.ValueKind != JsonValueKind.Array
            || (int)vout >= outputs.GetArrayLength())
        {
            throw new InvalidOperationException($"mempool.space response for {txid} has no vout[{vout}].");
        }
        if (!outputs[(int)vout].TryGetProperty("value", out var valueEl))
        {
            throw new InvalidOperationException($"mempool.space response for {txid}:{vout} missing 'value' field.");
        }
        return valueEl.GetInt64();
    }
}
