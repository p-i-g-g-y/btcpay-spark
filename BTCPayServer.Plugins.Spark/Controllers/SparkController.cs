using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.Spark.Configuration;
using BTCPayServer.Plugins.Spark.Data.Entities;
using BTCPayServer.Plugins.Spark.Exceptions;
using BTCPayServer.Plugins.Spark.Helpers;
using BTCPayServer.Plugins.Spark.Lightning;
using BTCPayServer.Plugins.Spark.Models;
using BTCPayServer.Plugins.Spark.Services;
using BTCPayServer.Security;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NSpark;
using NSpark.Services;

namespace BTCPayServer.Plugins.Spark.Controllers;

[Route("plugins/spark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SparkController(
    SparkWalletService walletService,
    IStoreSparkWalletProvider walletProvider,
    IInvoiceMappingService invoiceMapping,
    PaymentMethodHandlerDictionary paymentMethodHandlers,
    StoreRepository storeRepository,
    IAuthorizationService authorizationService,
    SparkNetworkConfig networkConfig,
    SparkConnection sparkConnection,
    BTCPayNetworkProvider networkProvider,
    Microsoft.EntityFrameworkCore.IDbContextFactory<Data.SparkPluginDbContext> dbContextFactory,
    SparkDepositSignal depositSignal,
    ISparkTransferActivityService transferActivity,
    IOutgoingPaymentService outgoingPayments,
    ILogger<SparkController> logger) : Controller
{
    private static readonly PaymentMethodId LightningPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");

    private Network BtcNetwork => networkProvider.BTC.NBitcoinNetwork;

    /// <summary>
    /// Anonymous diagnostic endpoint. Hit <c>/plugins/spark/ping</c> in a browser:
    /// 200 + plain text = plugin routes are registered.
    /// 404 = the plugin's controllers aren't being discovered by BTCPay's MVC route table — that
    /// would also explain why <c>onclick="location.href='/plugins/spark/.../enable-ln'"</c>
    /// appears to do nothing (it navigates to a 404 the browser may render briefly before
    /// snapping back to where you were).
    /// </summary>
    [HttpGet("ping")]
    [AllowAnonymous]
    public IActionResult Ping() => Content("Spark plugin route OK", "text/plain");

    // -----------------------------------------------------------------------
    // Initial setup (wallet creation / import)
    // -----------------------------------------------------------------------

    [HttpGet("stores/{storeId}/initial-setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> InitialSetup(string storeId, CancellationToken ct)
    {
        var store = HttpContext.GetStoreData();
        if (store == null) return NotFound();

        var existing = await walletService.GetByStoreAsync(storeId, ct);
        if (existing is not null)
            return RedirectToAction(nameof(StoreOverview), new { storeId });

        return View(new InitialWalletSetupViewModel());
    }

    [HttpPost("stores/{storeId}/initial-setup")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> InitialSetup(string storeId, InitialWalletSetupViewModel model, CancellationToken ct)
    {
        var store = HttpContext.GetStoreData();
        if (store == null) return NotFound();

        string mnemonic;
        var newlyGenerated = false;
        try
        {
            if (string.IsNullOrWhiteSpace(model.Mnemonic))
            {
                mnemonic = SparkWalletFactory.GenerateMnemonic();
                newlyGenerated = true;
            }
            else
            {
                mnemonic = SparkWalletFactory.NormalizeAndValidateMnemonic(model.Mnemonic);
            }
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(nameof(model.Mnemonic), ex.Message);
            return View(model);
        }

        SparkWalletEntity entity;
        try
        {
            entity = await walletService.UpsertWalletAsync(storeId, mnemonic, model.Passphrase, account: null, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Spark wallet for store {StoreId}", storeId);
            ModelState.AddModelError(nameof(model.Mnemonic), $"Failed to initialise Spark wallet: {ex.Message}");
            return View(model);
        }

        // Eagerly do two post-creation tasks. Both are non-fatal — the wallet is usable without
        // them and they can be retried (auto-claimer for the deposit address, Settings page for
        // privacy):
        //   1) Derive the wallet's deterministic static deposit address so the overview shows it
        //      immediately (otherwise the user waits up to ~60s for the auto-claimer's sweep).
        //   2) Opt the wallet into Spark's server-side privacy mode so it's not exposed in
        //      Spark explorers / public indices by default.
        try
        {
            var wallet = await walletProvider.GetByWalletIdAsync(entity.Id, ct);
            if (wallet is not null)
            {
                try
                {
                    var sda = await wallet.GetStaticDepositAddressAsync(ct);
                    await walletService.SetStaticDepositAddressAsync(entity.Id, sda.Address, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Eager static deposit derivation failed for wallet {WalletId}", entity.Id);
                }

                try
                {
                    await wallet.SetPrivacyEnabledAsync(enabled: true, ct);
                    logger.LogInformation("Privacy mode enabled for new Spark wallet {WalletId}", entity.Id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SetPrivacyEnabledAsync failed for wallet {WalletId} — admin can re-enable from Settings", entity.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-creation setup tasks failed for wallet {WalletId}", entity.Id);
        }

        // Auto-enable Lightning with our connection string. We don't touch other LN configs the
        // admin may already have wired up.
        var existingLn = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(LightningPaymentMethodId, paymentMethodHandlers);
        var connStr = $"type={SparkLightningConnectionStringHandler.ConnectionStringType};wallet-id={entity.Id}";
        if (existingLn is null || string.IsNullOrEmpty(existingLn.ConnectionString))
        {
            store.SetPaymentMethodConfig(paymentMethodHandlers[LightningPaymentMethodId],
                new LightningPaymentMethodConfig { ConnectionString = connStr });

            var blob = store.GetStoreBlob();
            blob.SetExcluded(LightningPaymentMethodId, false);
            store.SetStoreBlob(blob);
        }
        await storeRepository.UpdateStore(store);

        if (newlyGenerated)
        {
            return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
            {
                ReturnUrl = Url.Action(nameof(StoreOverview), new { storeId }),
                IsStored = true,
                RequireConfirm = true,
                CryptoCode = "BTC",
                Mnemonic = mnemonic,
            });
        }

        TempData[WellKnownTempData.SuccessMessage] = "Spark wallet configured.";
        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    // -----------------------------------------------------------------------
    // Store overview
    // -----------------------------------------------------------------------

    [HttpGet("stores/{storeId}/overview")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> StoreOverview(string storeId, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        SparkBalancesViewModel? balances = null;
        ServiceConnectionStatus sspStatus;
        try
        {
            var wallet = await walletProvider.GetByWalletIdAsync(entity!.Id, ct);
            if (wallet is not null)
            {
                var bal = await wallet.GetBalanceAsync(ct);
                balances = new SparkBalancesViewModel
                {
                    AvailableSats = bal.SatsBalance.Available,
                    OwnedSats = bal.SatsBalance.Owned,
                    IncomingSats = bal.SatsBalance.Incoming,
                    LeafCount = bal.Leaves.Count,
                };
            }
            sspStatus = new ServiceConnectionStatus { Name = "Spark SSP", Endpoint = sparkConnection.Options.SspUrl, Connected = true };
        }
        catch (Exception ex)
        {
            sspStatus = new ServiceConnectionStatus { Name = "Spark SSP", Endpoint = sparkConnection.Options.SspUrl, Connected = false, Error = ex.Message };
        }

        var canManagePrivateKeys = (await authorizationService.AuthorizeAsync(User, null,
            new PolicyRequirement(Policies.CanModifyServerSettings))).Succeeded;

        await using var dbctx = await dbContextFactory.CreateDbContextAsync(ct);
        var recentInvoices = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(dbctx.LightningInvoices.AsNoTracking()
                .Where(i => i.WalletId == entity!.Id)
                .OrderByDescending(i => i.CreatedAt)
                .Take(5), ct);
        var recentOutgoingPayments = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(dbctx.OutgoingPayments.AsNoTracking()
                .Where(p => p.WalletId == entity!.Id)
                .OrderByDescending(p => p.CreatedAt)
                .Take(5), ct);
        var recentDeposits = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(dbctx.WalletDeposits.AsNoTracking()
                .Where(d => d.WalletId == entity!.Id)
                .OrderByDescending(d => d.CreatedAt)
                .Take(5), ct);
        var recentWithdrawals = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(dbctx.WalletWithdrawals.AsNoTracking()
                .Where(w => w.WalletId == entity!.Id)
                .OrderByDescending(w => w.CreatedAt)
                .Take(5), ct);

        // Authoritative "all" feed — straight from the Signing Operators. Done after the local
        // queries so a slow SO call doesn't delay the rest of the page; a null result is fine
        // because the view falls back to the local feeds via tabs.
        var recentTransfers = await transferActivity.GetActivityPageAsync(entity!.Id, take: 5, ct: ct);

        var lnEnabled = IsLightningConfigured();

        // Project DB entities to presentation rows here (single source of truth) so every
        // surface — Overview, History, Dashboard widget — renders identical row layout.
        var vm = new StoreOverviewViewModel
        {
            StoreId = storeId,
            WalletId = entity!.Id,
            SparkAddress = entity.SparkAddress,
            IdentityPublicKeyHex = entity.IdentityPublicKeyHex,
            StaticDepositAddress = entity.StaticDepositAddress,
            Network = entity.Network.ToString(),
            Balances = balances,
            IsLightningEnabled = lnEnabled,
            CanManagePrivateKeys = canManagePrivateKeys,
            RecentInvoiceRows = recentInvoices.Select(SparkActivityRowFactory.FromInvoice).ToList(),
            RecentInvoiceCount = recentInvoices.Count,
            RecentOutgoingPaymentRows = recentOutgoingPayments.Select(SparkActivityRowFactory.FromOutgoingPayment).ToList(),
            RecentOutgoingPaymentCount = recentOutgoingPayments.Count,
            RecentDepositRows = recentDeposits.Select(d => SparkActivityRowFactory.FromDeposit(d, BtcNetwork)).ToList(),
            RecentDepositCount = recentDeposits.Count,
            RecentWithdrawalRows = recentWithdrawals.Select(w => SparkActivityRowFactory.FromWithdrawal(w, BtcNetwork)).ToList(),
            RecentWithdrawalCount = recentWithdrawals.Count,
            RecentTransfers = recentTransfers,
            Services = [sspStatus],
        };
        return View(vm);
    }

    // -----------------------------------------------------------------------
    // Derive static deposit address (fallback if eager derivation on InitialSetup failed)
    // -----------------------------------------------------------------------

    [HttpPost("stores/{storeId}/derive-static-address")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DeriveStaticAddress(string storeId, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        try
        {
            var wallet = await walletProvider.GetByWalletIdAsync(entity!.Id, ct);
            if (wallet is null) throw new SparkWalletNotConfiguredException(storeId);

            var sda = await wallet.GetStaticDepositAddressAsync(ct);
            await walletService.SetStaticDepositAddressAsync(entity.Id, sda.Address, ct);
            TempData[WellKnownTempData.SuccessMessage] = $"Static deposit address ready: {sda.Address}";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to derive static deposit address: {ex.Message}";
        }
        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    /// <summary>
    /// Resets a deposit's claim backoff so the auto-claimer retries it immediately on its next
    /// pass. Useful when a deposit has been stuck (e.g. SSP was down) and the admin knows it
    /// should now succeed. Also resets <see cref="SparkWalletDepositEntity.Status"/> back to
    /// <see cref="SparkWalletDepositStatus.Discovered"/> if it had been manually marked
    /// <see cref="SparkWalletDepositStatus.Failed"/>.
    /// </summary>
    [HttpPost("stores/{storeId}/deposits/{depositId}/retry")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> RetryDeposit(string storeId, string depositId, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        await using var dbctx = await dbContextFactory.CreateDbContextAsync(ct);
        var deposit = await dbctx.WalletDeposits
            .FirstOrDefaultAsync(d => d.Id == depositId && d.WalletId == entity!.Id, ct);
        if (deposit is null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Deposit not found.";
            return RedirectToAction(nameof(History), new { storeId });
        }

        if (deposit.Status is SparkWalletDepositStatus.Failed)
            deposit.Status = SparkWalletDepositStatus.Discovered;
        deposit.ClaimAttempt = 0;
        deposit.LastError = null;
        await dbctx.SaveChangesAsync(ct);

        // Wake the auto-claimer immediately so the user doesn't wait up to a full sweep period.
        depositSignal.Signal(entity!.Id);

        TempData[WellKnownTempData.SuccessMessage] = "Retry queued — the auto-claimer will attempt this deposit on the next sweep.";
        return RedirectToAction(nameof(History), new { storeId });
    }

    // -----------------------------------------------------------------------
    // Receive (Spark address, on-chain deposit address, Lightning invoice generator)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Three-section receive page:
    /// <list type="number">
    ///   <item>Spark address — stable, off-chain Spark-to-Spark.</item>
    ///   <item>On-chain deposit address — reusable, UTXOs auto-claim after 3 confirmations.</item>
    ///   <item>Lightning invoice generator — produces a fresh BOLT11 on POST.</item>
    /// </list>
    /// The first two are stable and shown immediately; the third is an empty form until POST.
    /// </summary>
    [HttpGet("stores/{storeId}/receive")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        return View(new SparkReceiveViewModel
        {
            StoreId = storeId,
            SparkAddress = entity!.SparkAddress,
            StaticDepositAddress = entity.StaticDepositAddress,
        });
    }

    [HttpPost("stores/{storeId}/receive")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(string storeId, SparkReceiveViewModel model, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        // Re-populate the always-visible fields whether the POST succeeds or fails — we re-render
        // the same view either way.
        model.StoreId = storeId;
        model.SparkAddress = entity!.SparkAddress;
        model.StaticDepositAddress = entity.StaticDepositAddress;
        model.ActiveTab = "lightning";

        if (model.InvoiceAmountSats is null || model.InvoiceAmountSats <= 0)
        {
            ModelState.AddModelError(nameof(model.InvoiceAmountSats), "Enter the amount you want to receive (in sats).");
        }
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var wallet = await walletProvider.GetByWalletIdAsync(entity.Id, ct);
            if (wallet is null) throw new SparkWalletNotConfiguredException(storeId);

            var expirySecs = (int)TimeSpan.FromMinutes(model.InvoiceExpiryMinutes).TotalSeconds;
            // Persisted via IInvoiceMappingService so the resulting BOLT11 is tracked by both
            // SparkInvoicePoller and SparkEventSubscriber — identical lifecycle to invoices
            // BTCPay creates for customer checkout. Shows up under the "Lightning invoices" tab.
            var sparkInvoice = await wallet.CreateLightningInvoiceAsync(
                amountSats: model.InvoiceAmountSats!.Value,
                memo: string.IsNullOrWhiteSpace(model.InvoiceMemo) ? null : model.InvoiceMemo,
                expirySecs: expirySecs,
                receiverIdentityPublicKey: null,
                descriptionHash: null,
                ct: ct);

            await invoiceMapping.UpsertAsync(new SparkLightningInvoiceEntity
            {
                RequestId = sparkInvoice.RequestId ?? sparkInvoice.PaymentHash,
                WalletId = entity.Id,
                PaymentHash = sparkInvoice.PaymentHash,
                Bolt11 = sparkInvoice.PaymentRequest,
                AmountSats = sparkInvoice.AmountSats,
                Memo = model.InvoiceMemo,
                Currency = "BTC",
                Status = SparkLightningInvoiceStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = sparkInvoice.ExpiresAt,
                LastPolledAt = DateTimeOffset.UtcNow,
            }, ct);

            model.GeneratedInvoice = sparkInvoice.PaymentRequest;
            model.GeneratedInvoicePaymentHash = sparkInvoice.PaymentHash;
            model.GeneratedInvoiceAmountSats = sparkInvoice.AmountSats;
            model.GeneratedInvoiceExpiresAt = sparkInvoice.ExpiresAt;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lightning invoice generation failed for store {StoreId}", storeId);
            ModelState.AddModelError("", $"Failed to generate invoice: {ex.Message}");
        }
        return View(model);
    }

    /// <summary>
    /// JSON polling endpoint for the Receive page. The client polls every few seconds while the
    /// page is open; we hand back the current state of the in-progress invoice (if any) plus the
    /// top-N incoming activity rows per channel (Spark / Lightning / on-chain). The client uses
    /// the first to flip the Lightning tab from "Waiting" → "Paid" without a page reload, and the
    /// second to live-update the "Recent receives" panels on the other tabs.
    /// </summary>
    /// <remarks>
    /// Returns a small, view-agnostic JSON payload. Designed to be cheap: invoice lookup hits the
    /// local DB only, and the transfer query is the same call the History page makes. Polling
    /// rate is controlled by the client (default ~4s); we don't enforce throttling here because
    /// the page is per-user behind store-settings auth and the workload is trivial.
    /// </remarks>
    [HttpGet("stores/{storeId}/receive/status")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ReceiveStatus(string storeId, string? paymentHash, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        // Invoice status — only meaningful when the page was rendered with a freshly-generated
        // invoice. The client passes its paymentHash to scope this query.
        object? invoice = null;
        if (!string.IsNullOrEmpty(paymentHash))
        {
            var inv = await invoiceMapping.GetByPaymentHashAsync(entity!.Id, paymentHash, ct);
            if (inv is not null)
            {
                invoice = new
                {
                    status = inv.Status.ToString(),
                    paidAt = inv.PaidAt,
                    amountSats = inv.AmountSats,
                };
            }
        }

        // Top incoming rows from the SO-authoritative transfer feed, bucketed per channel so the
        // client can update each tab's "Recent receives" panel independently. We over-fetch
        // (take 20) to make sure each bucket has at least a couple of rows after kind filtering.
        IReadOnlyList<object> lightning = [], spark = [], onchain = [];
        try
        {
            var page = await transferActivity.GetActivityPageAsync(entity!.Id, take: 20, ct: ct);
            var incoming = page.Rows.Where(r => r.Direction == SparkActivityDirection.Incoming).ToList();
            lightning = incoming.Where(r => r.Kind == "Lightning payment").Take(5).Select(Project).ToList();
            spark = incoming.Where(r => r.Kind == "Spark transfer").Take(5).Select(Project).ToList();
            onchain = incoming.Where(r => r.Kind == "On-chain deposit").Take(5).Select(Project).ToList();
        }
        catch
        {
            // Transient SO failures don't break the page — the next poll will retry.
        }

        return Json(new
        {
            invoice,
            incoming = new { lightning, spark, onchain },
            // ISO timestamp the client can use to ignore stale rows once we add a "since" filter.
            now = DateTimeOffset.UtcNow,
        });

        static object Project(SparkActivityRow r) => new
        {
            kind = r.Kind,
            kindBadge = r.KindBadgeClass,
            amountSats = r.AmountSats,
            statusLabel = r.StatusLabel,
            statusBadge = r.StatusBadgeClass,
            at = r.At,
            detail = r.Detail,
        };
    }

    // -----------------------------------------------------------------------
    // Send (Lightning + Spark transfer + on-chain withdraw)
    // -----------------------------------------------------------------------

    [HttpGet("stores/{storeId}/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(string storeId, CancellationToken ct)
    {
        var (_, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;
        return View(new SparkSendViewModel());
    }

    /// <summary>
    /// Single POST endpoint backing the multi-step Send wizard. Branches on the <c>command</c>
    /// field submitted by the form:
    /// <list type="bullet">
    ///   <item><c>parse</c> — coming from the Destination step. Classify the destination, decode
    ///         BOLT11 amount/memo if present, advance to Amount or skip straight to Confirm.</item>
    ///   <item><c>quote</c> — coming from the Amount step. Validate the amount and fetch the real
    ///         fee from the SO/SSP before advancing to Confirm.</item>
    ///   <item><c>confirm</c> — coming from the Confirm step. Execute the send and advance to Done.</item>
    ///   <item><c>back</c> — re-render the previous step without doing any work, preserving
    ///         already-entered values so the user doesn't lose progress.</item>
    /// </list>
    /// State is carried entirely via hidden form fields, so browser back/forward works naturally.
    /// </summary>
    [HttpPost("stores/{storeId}/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(
        string storeId,
        SparkSendViewModel model,
        [FromForm] string? command,
        CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        var wallet = await walletProvider.GetByWalletIdAsync(entity!.Id, ct);
        if (wallet is null)
        {
            ModelState.AddModelError("", "Spark wallet is unavailable.");
            model.Step = SparkSendStep.Destination;
            return View(model);
        }

        // ── back button ─────────────────────────────────────────────────────
        if (string.Equals(command, "back", StringComparison.OrdinalIgnoreCase))
        {
            model.Step = model.Step switch
            {
                SparkSendStep.Confirm when model.AmountLocked => SparkSendStep.Destination,
                SparkSendStep.Confirm => SparkSendStep.Amount,
                SparkSendStep.Amount => SparkSendStep.Destination,
                _ => SparkSendStep.Destination,
            };
            ModelState.Clear();
            return View(model);
        }

        // ── parse: Destination → (Amount | Confirm) ────────────────────────
        if (string.Equals(command, "parse", StringComparison.OrdinalIgnoreCase))
        {
            var destination = (model.Destination ?? "").Trim();
            if (string.IsNullOrEmpty(destination))
            {
                ModelState.AddModelError(nameof(model.Destination), "Paste an invoice, address, or lightning address.");
                model.Step = SparkSendStep.Destination;
                return View(model);
            }
            model.Destination = destination;

            // Reset fields populated during a previous parse so swapping the destination doesn't
            // leak stale data.
            model.Kind = SparkSendKind.Unknown;
            model.AmountLocked = false;
            model.Memo = null;
            model.FeeEstimateSats = null;
            if (!model.AmountLocked) model.AmountSats = null;

            try
            {
                if (LooksLikeBolt11(destination))
                {
                    model.Kind = SparkSendKind.Bolt11;
                    var pr = BOLT11PaymentRequest.Parse(destination, BtcNetwork);
                    var bolt11Sats = pr.MinimumAmount is null
                        ? (long?)null
                        : (long)pr.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
                    model.Memo = pr.ShortDescription;
                    if (bolt11Sats is > 0)
                    {
                        // BOLT11 fully specifies the amount — skip Amount step.
                        model.AmountSats = bolt11Sats;
                        model.AmountLocked = true;
                        await PopulateBalanceAndFeeAsync(wallet, model, ct);
                        model.Step = SparkSendStep.Confirm;
                        return View(model);
                    }
                    // Zero-amount BOLT11 — user must enter amount.
                    model.Step = SparkSendStep.Amount;
                    return View(model);
                }

                if (destination.Contains('@') && !destination.StartsWith("@"))
                {
                    model.Kind = SparkSendKind.LightningAddress;
                    model.Step = SparkSendStep.Amount;
                    return View(model);
                }

                if (destination.StartsWith("spark1", StringComparison.OrdinalIgnoreCase)
                    || destination.StartsWith("sparkrt1", StringComparison.OrdinalIgnoreCase))
                {
                    // Throws on malformed address — caught below.
                    _ = SparkAddress.DecodeIdentityPublicKey(destination);
                    model.Kind = SparkSendKind.SparkAddress;
                    model.Step = SparkSendStep.Amount;
                    return View(model);
                }

                // Last resort — try parsing as a BTC address. Throws on invalid.
                BitcoinAddress.Create(destination, BtcNetwork);
                model.Kind = SparkSendKind.OnChain;
                model.Step = SparkSendStep.Amount;
                return View(model);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(nameof(model.Destination),
                    $"Couldn't recognise that as a BOLT11 invoice, lightning address, Spark address, or Bitcoin address. ({ex.Message})");
                model.Kind = SparkSendKind.Unknown;
                model.Step = SparkSendStep.Destination;
                return View(model);
            }
        }

        // ── quote: Amount → Confirm ────────────────────────────────────────
        if (string.Equals(command, "quote", StringComparison.OrdinalIgnoreCase))
        {
            if (model.Kind == SparkSendKind.Unknown || string.IsNullOrEmpty(model.Destination))
            {
                // Lost state — restart.
                model.Step = SparkSendStep.Destination;
                return View(model);
            }

            // "Send all" mode (on-chain only). The user didn't type an amount — we'll fetch the
            // current balance, build a fee quote against ALL available leaves, and treat the
            // displayed amount as `balance − fee` (what the recipient actually receives). The
            // execute step then sweeps every leaf via WithdrawAsync(addr, balance).
            // For other kinds (Lightning, Spark) "swipe all" has no meaning, so silently fall
            // through to the standard amount-validation path.
            if (model.SwipeAll && model.Kind == SparkSendKind.OnChain)
            {
                try
                {
                    await PopulateSwipeAllAsync(wallet, model, ct);
                    if ((model.AmountSats ?? 0) <= 0)
                    {
                        ModelState.AddModelError("", "Wallet has no spendable balance to sweep.");
                        model.Step = SparkSendStep.Amount;
                        return View(model);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Spark swipe-all quote failed (wallet={WalletId})", entity.Id);
                    ModelState.AddModelError("", $"Couldn't compute a sweep quote: {ex.Message}");
                    model.Step = SparkSendStep.Amount;
                    return View(model);
                }
                model.Step = SparkSendStep.Confirm;
                return View(model);
            }

            if ((model.AmountSats ?? 0) <= 0)
            {
                ModelState.AddModelError(nameof(model.AmountSats), "Enter an amount greater than zero.");
                model.Step = SparkSendStep.Amount;
                return View(model);
            }
            try
            {
                await PopulateBalanceAndFeeAsync(wallet, model, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Spark send fee quote failed (wallet={WalletId})", entity.Id);
                // Non-fatal — show the confirm step with a null fee estimate; the actual fee will
                // be paid at execute time.
                model.FeeEstimateSats = null;
                ModelState.AddModelError("", $"Couldn't fetch a fee estimate. The actual fee will be deducted at send time. ({ex.Message})");
            }
            model.Step = SparkSendStep.Confirm;
            return View(model);
        }

        // ── confirm: Confirm → Done (the actual send) ──────────────────────
        if (string.Equals(command, "confirm", StringComparison.OrdinalIgnoreCase))
        {
            if (model.Kind == SparkSendKind.Unknown || string.IsNullOrEmpty(model.Destination)
                || (model.AmountSats ?? 0) <= 0 && model.Kind != SparkSendKind.Bolt11)
            {
                model.Step = SparkSendStep.Destination;
                return View(model);
            }
            try
            {
                model.Result = await ExecuteSendAsync(entity!, wallet, model, ct);
                model.Step = SparkSendStep.Done;
                return View(model);
            }
            catch (NSpark.Exceptions.SparkConnectionException scx)
                when (scx.GrpcStatusCode == Grpc.Core.StatusCode.AlreadyExists)
            {
                // The Spark coordinator rejected this send before it left the wallet — it's still
                // finalising a prior transfer that consumed (or is consuming) the same leaves. The
                // Lightning invoice was NOT paid by this attempt.
                //
                // We MUST NOT leave the user on the Confirm step here. Re-clicking Send would hit
                // EnsurePendingAsync's idempotency dedup (same WalletId + PaymentHash) and return
                // the row that PayLightningInvoiceAsync just failed against — which has no
                // SspRequestId — and the wizard would render the Done step with an empty request
                // id, looking like success. Abandon the wizard entirely and force the user to
                // start a fresh Send flow (re-paste the destination, re-enter the amount) once
                // the coordinator releases the leaves.
                logger.LogWarning(scx,
                    "Spark Send wizard hit AlreadyExists (wallet={WalletId}, kind={Kind}) — abandoning the wizard.",
                    entity!.Id, model.Kind);
                TempData[WellKnownTempData.ErrorMessage] =
                    "Spark coordinator is still finalising a previous transfer from this wallet — the leaves needed for this send are not yet released. " +
                    "The Lightning invoice was NOT paid. Wait a few seconds and start a new Send flow.";
                return RedirectToAction(nameof(Send), new { storeId });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Spark send failed (wallet={WalletId}, kind={Kind})", entity!.Id, model.Kind);
                ModelState.AddModelError("", $"Send failed: {ex.Message}");
                model.Step = SparkSendStep.Confirm;
                return View(model);
            }
        }

        // Unknown command — render current step as a fallback.
        return View(model);
    }

    /// <summary>
    /// Populates <see cref="SparkSendViewModel.AvailableSats"/> and
    /// <see cref="SparkSendViewModel.FeeEstimateSats"/> for the Confirm step. Different transfer
    /// kinds use different SSP endpoints:
    /// <list type="bullet">
    ///   <item><b>BOLT11</b> → <c>GetLightningSendFeeEstimateAsync</c></item>
    ///   <item><b>Lightning address</b> → uses BOLT11 estimate as a proxy; the actual invoice
    ///         isn't fetched until execute time. We surface this as "estimated".</item>
    ///   <item><b>Spark address</b> → no fee (off-chain Spark-to-Spark is instant + free)</item>
    ///   <item><b>On-chain</b> → <c>GetFeeQuoteAsync</c> against leaves we'd be paying with.</item>
    /// </list>
    /// </summary>
    private async Task PopulateBalanceAndFeeAsync(NSpark.SparkWallet wallet, SparkSendViewModel model, CancellationToken ct)
    {
        var balance = await wallet.GetBalanceAsync(ct);
        model.AvailableSats = balance.SatsBalance.Available;

        switch (model.Kind)
        {
            case SparkSendKind.Bolt11:
                model.FeeEstimateSats = await wallet.GetLightningSendFeeEstimateAsync(model.Destination!, ct: ct);
                break;
            case SparkSendKind.LightningAddress:
                // Walk the LNURL-pay protocol now so we can estimate the exact fee for the
                // BOLT11 the destination will hand us. We lock that resolved BOLT11 in the model
                // and pay it verbatim at execute time — re-resolving on confirm could return a
                // different invoice (LNURL endpoints are one-shot by spec).
                {
                    var amt = model.AmountSats ?? 0;
                    if (amt <= 0) { model.FeeEstimateSats = null; break; }
                    var resolved = await wallet.ResolveLightningAddressAsync(model.Destination!, amt, ct);
                    model.ResolvedBolt11 = resolved;
                    try
                    {
                        var pr = BOLT11PaymentRequest.Parse(resolved, BtcNetwork);
                        if (string.IsNullOrEmpty(model.Memo))
                            model.Memo = pr.ShortDescription;
                    }
                    catch { /* memo is best-effort */ }
                    model.FeeEstimateSats = await wallet.GetLightningSendFeeEstimateAsync(resolved, ct: ct);
                }
                break;
            case SparkSendKind.SparkAddress:
                model.FeeEstimateSats = 0; // Spark-to-Spark is free.
                break;
            case SparkSendKind.OnChain:
                // ON-CHAIN FEE SEMANTICS (important — different from Lightning):
                //
                // NSpark's WithdrawAsync(addr, X) selects leaves totalling exactly X and the SSP
                // sweeps them all via `withdraw_all: true`. The recipient receives X − fee.
                // From the *user's* perspective on this Send wizard, the amount they entered is
                // "what the recipient should receive" — same as Lightning. To honour that, we
                // execute with `WithdrawAsync(addr, amount + fee)` so the recipient gets `amount`.
                //
                // We need a leaf set to ask the SSP for a fee quote (the fee depends on leaf
                // count, since each leaf becomes a coop-exit input). Two-pass approach:
                //   1) pick leaves covering `amount` to get an initial fee guess
                //   2) re-pick covering `amount + fee` to lock in the leaf set that's actually
                //      going to be used at execute time; re-quote against that
                // The two passes converge instantly because adding one extra leaf changes the
                // fee by a fixed per-leaf amount that's far smaller than typical leaf values.
                {
                    var amount = model.AmountSats ?? 0;
                    var availableLeaves = balance.Leaves
                        .Where(l => string.Equals(l.Status, "AVAILABLE", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(l => l.ValueSats)
                        .ToList();

                    string[]? PickLeavesCovering(long target)
                    {
                        var ids = new List<string>();
                        long covered = 0;
                        foreach (var leaf in availableLeaves)
                        {
                            ids.Add(leaf.Id);
                            covered += leaf.ValueSats;
                            if (covered >= target) return ids.ToArray();
                        }
                        return null; // can't cover from existing leaves
                    }

                    var firstPick = PickLeavesCovering(amount);
                    if (firstPick is null)
                    {
                        // Can't even cover the bare amount with existing leaves. Either the user
                        // doesn't have the balance, or the wallet would need a leaf swap at send
                        // time (rare on a healthy wallet). Either way, no quote possible here —
                        // execute will fail loudly if it's the former.
                        model.FeeEstimateSats = null;
                        break;
                    }
                    var firstQuote = await wallet.GetFeeQuoteAsync(firstPick, model.Destination!, ct);
                    var secondPick = PickLeavesCovering(amount + firstQuote.FeeSats);
                    if (secondPick is null || secondPick.Length == firstPick.Length)
                    {
                        // No additional leaf needed — first quote is the real fee.
                        model.FeeEstimateSats = firstQuote.FeeSats;
                        break;
                    }
                    var secondQuote = await wallet.GetFeeQuoteAsync(secondPick, model.Destination!, ct);
                    model.FeeEstimateSats = secondQuote.FeeSats;
                }
                break;
        }
    }

    /// <summary>
    /// "Send all available" quote for on-chain withdrawals. Differs from
    /// <see cref="PopulateBalanceAndFeeAsync"/> in two ways: (1) we fetch a fee quote against
    /// EVERY available leaf, not just enough to cover an amount the user typed; (2) the
    /// displayed amount is <c>balance − fee</c> so the user sees what the recipient receives.
    /// On execute the controller passes the full balance to <c>WithdrawAsync</c>, and the SSP's
    /// <c>withdraw_all: true</c> mode burns every selected leaf.
    /// </summary>
    private async Task PopulateSwipeAllAsync(NSpark.SparkWallet wallet, SparkSendViewModel model, CancellationToken ct)
    {
        var balance = await wallet.GetBalanceAsync(ct);
        var available = balance.SatsBalance.Available;
        model.AvailableSats = available;

        var allIds = balance.Leaves
            .Where(l => string.Equals(l.Status, "AVAILABLE", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Id)
            .ToArray();
        if (allIds.Length == 0)
        {
            model.AmountSats = 0;
            model.FeeEstimateSats = null;
            return;
        }

        var quote = await wallet.GetFeeQuoteAsync(allIds, model.Destination!, ct);
        var fee = Math.Max(0, quote.FeeSats);

        // Display amount = what the recipient gets after the SSP carves off its fee.
        var recipientGets = available - fee;
        model.AmountSats = Math.Max(0, recipientGets);
        model.FeeEstimateSats = fee;
    }

    /// <summary>Performs the actual send and writes any plugin-side bookkeeping rows.</summary>
    private async Task<SparkSendResultViewModel> ExecuteSendAsync(
        Data.Entities.SparkWalletEntity entity,
        NSpark.SparkWallet wallet,
        SparkSendViewModel model,
        CancellationToken ct)
    {
        var destination = model.Destination!;
        switch (model.Kind)
        {
            case SparkSendKind.Bolt11:
            {
                var sspRequestId = await TrackedLightningSendAsync(entity, wallet, destination, model.AmountSats, model.MaxFeeSats, model.FeeEstimateSats, ct);
                logger.LogInformation("Spark LN pay (wallet={WalletId}, request={SspRequestId})", entity.Id, sspRequestId);
                return new SparkSendResultViewModel
                {
                    Kind = SparkSendKind.Bolt11,
                    Destination = destination,
                    AmountSats = model.AmountSats ?? 0,
                    FeeSats = model.FeeEstimateSats,
                    SspRequestId = sspRequestId,
                };
            }
            case SparkSendKind.LightningAddress:
            {
                // Pay the exact BOLT11 we resolved + priced on the quote step. Falling back to
                // PayLightningAddressAsync (which re-resolves LNURL) would risk paying a
                // different invoice than the one we showed the user a fee for.
                var bolt11 = !string.IsNullOrEmpty(model.ResolvedBolt11)
                    ? model.ResolvedBolt11
                    : await wallet.ResolveLightningAddressAsync(destination, model.AmountSats!.Value, ct);
                var sspRequestId = await TrackedLightningSendAsync(entity, wallet, bolt11, model.AmountSats, model.MaxFeeSats, model.FeeEstimateSats, ct);
                return new SparkSendResultViewModel
                {
                    Kind = SparkSendKind.LightningAddress,
                    Destination = destination,
                    AmountSats = model.AmountSats!.Value,
                    FeeSats = model.FeeEstimateSats,
                    SspRequestId = sspRequestId,
                };
            }
            case SparkSendKind.SparkAddress:
            {
                var receiverPubKey = SparkAddress.DecodeIdentityPublicKey(destination);
                var transfer = await wallet.SendAsync(receiverPubKey, model.AmountSats!.Value, transferId: null, ct);
                logger.LogInformation("Spark transfer sent (wallet={WalletId}, transferId={TransferId})", entity.Id, transfer.Id);
                return new SparkSendResultViewModel
                {
                    Kind = SparkSendKind.SparkAddress,
                    Destination = destination,
                    AmountSats = model.AmountSats!.Value,
                    FeeSats = 0,
                    SparkTransferId = transfer.Id,
                };
            }
            case SparkSendKind.OnChain:
            {
                // Two on-chain modes:
                //   Standard send: user typed "send X". NSpark's WithdrawAsync(addr, N) burns N
                //       sats and the SSP delivers `N − fee` — so we pass `amount + fee` to make
                //       the recipient receive exactly the amount the user typed (Lightning parity).
                //   Swipe all: user wants to empty the wallet. We pass the full available balance
                //       directly; SSP's `withdraw_all: true` sweeps every leaf. Recipient gets
                //       `balance − fee` and the model's AmountSats already encodes that.
                // FeeEstimateSats null → quote unavailable; we still try the send with the raw
                // amount and let the SSP reject if the wallet can't cover the actual fee.
                var amount = model.AmountSats!.Value;
                var fee = model.FeeEstimateSats ?? 0;
                long debit;
                if (model.SwipeAll && model.AvailableSats is { } avail)
                {
                    debit = avail;
                }
                else
                {
                    debit = amount + fee;
                }
                var txId = await wallet.WithdrawAsync(destination, debit, ct);
                await using var dbctx = await dbContextFactory.CreateDbContextAsync(ct);
                dbctx.WalletWithdrawals.Add(new SparkWalletWithdrawalEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    WalletId = entity.Id,
                    DestinationAddress = destination,
                    // Persist the amount the recipient actually receives (= user's input, or
                    // balance−fee in swipe-all mode). The fee column carries the rest of the
                    // story so the History page can show amount / fee / total cleanly.
                    AmountSats = amount,
                    TxId = txId,
                    Status = SparkWalletWithdrawalStatus.Sent,
                    CompletedAt = DateTimeOffset.UtcNow,
                    InitiatedByUser = User?.Identity?.Name,
                });
                await dbctx.SaveChangesAsync(ct);
                return new SparkSendResultViewModel
                {
                    Kind = SparkSendKind.OnChain,
                    Destination = destination,
                    AmountSats = amount,
                    FeeSats = fee,
                    TxId = txId,
                };
            }
            default:
                throw new InvalidOperationException("Unknown send kind.");
        }
    }

    /// <summary>
    /// Submits a BOLT11 to NSpark and persists it into <c>SparkOutgoingPaymentEntity</c> so it
    /// flows through the same lifecycle as Lightning sends initiated by BTCPay's payout pipeline
    /// (visible on the History "Lightning payments" tab, reconciled by
    /// <c>SparkOutgoingPaymentPoller</c>, idempotent on retries via the unique (WalletId,
    /// PaymentHash) index).
    /// </summary>
    /// <remarks>
    /// Mirrors the bookkeeping done by <c>SparkLightningClient.Pay()</c> — the BTCPay-payout
    /// entry point — without the BTCPay-side <c>PayResponse</c> wrapping or its synchronous
    /// "wait for terminal" loop. The Send wizard already shows the user the result page via
    /// our own bookkeeping, so we don't need to block here; the background poller will flip the
    /// row to Succeeded / Failed within seconds.
    /// </remarks>
    private async Task<string> TrackedLightningSendAsync(
        Data.Entities.SparkWalletEntity entity,
        NSpark.SparkWallet wallet,
        string bolt11,
        long? amountSats,
        long? maxFeeSats,
        long? feeEstimateSats,
        CancellationToken ct)
    {
        BTCPayServer.Lightning.BOLT11PaymentRequest? pr;
        try
        {
            pr = BTCPayServer.Lightning.BOLT11PaymentRequest.Parse(bolt11, BtcNetwork);
        }
        catch
        {
            pr = null;
        }

        var paymentHash = pr?.PaymentHash?.ToString();
        if (string.IsNullOrEmpty(paymentHash))
        {
            // Without a payment hash we can't dedupe or store the row. Send anyway — this is
            // exotic enough (invalid BOLT11 that NSpark nonetheless accepts) that losing the
            // history row beats refusing the send.
            return await wallet.PayLightningInvoiceAsync(bolt11, maxFeeSats, ct);
        }

        var pending = new SparkOutgoingPaymentEntity
        {
            WalletId = entity.Id,
            PaymentHash = paymentHash,
            Bolt11 = bolt11,
            AmountSats = amountSats ?? (pr!.MinimumAmount is null
                ? null
                : (long)pr.MinimumAmount.ToUnit(BTCPayServer.Lightning.LightMoneyUnit.Satoshi)),
            MaxFeeSats = maxFeeSats,
            ActualFeeSats = null,
            Status = SparkOutgoingPaymentStatus.Pending,
            ExpiresAt = pr?.ExpiryDate,
            Memo = pr?.ShortDescription,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastPolledAt = DateTimeOffset.UtcNow,
        };
        var (row, inserted) = await outgoingPayments.EnsurePendingAsync(pending, ct);

        if (!inserted)
        {
            // Idempotency hit — the (WalletId, PaymentHash) pair has been seen before. This
            // happens on three legitimate paths and one trap:
            //   * legit: the wizard form was double-submitted (rapid double-click) — the prior
            //            call is still in flight with a real SspRequestId. Return it.
            //   * legit: a prior call already succeeded — Status=Succeeded with a real
            //            SspRequestId. Return it.
            //   * legit: BTCPay's Lightning payout pipeline already started this send via
            //            SparkLightningClient.Pay. Same thing — return its SspRequestId.
            //   * TRAP : a prior call hit AlreadyExists pre-submission and the row has no
            //            SspRequestId (we marked it Failed in our catch below). Blindly
            //            returning row.SspRequestId ?? "" used to make the wizard render the
            //            Done step with an empty request id, which the user reads as success.
            //            Refuse — surface as AlreadyExists so the wizard's confirm handler
            //            redirects the user to a fresh Send flow.
            if (!string.IsNullOrEmpty(row.SspRequestId))
                return row.SspRequestId;
            throw new NSpark.Exceptions.SparkConnectionException(
                "wallet.send.dedup",
                "A previous Send attempt for this exact invoice never reached the Spark coordinator. Start a new Send flow.")
            {
                GrpcStatusCode = Grpc.Core.StatusCode.AlreadyExists,
            };
        }

        try
        {
            var sspRequestId = await wallet.PayLightningInvoiceAsync(bolt11, maxFeeSats, ct);
            await outgoingPayments.AttachSspRequestIdAsync(row.Id, sspRequestId, ct);
            logger.LogInformation(
                "Spark Lightning send via wallet wizard (wallet={WalletId}, hash={Hash}, sspRequestId={SspRequestId})",
                entity.Id, paymentHash, sspRequestId);
            return sspRequestId;
        }
        catch (NSpark.Exceptions.SparkConnectionException scx)
            when (scx.GrpcStatusCode == Grpc.Core.StatusCode.AlreadyExists)
        {
            // Coordinator rejected the submission before the send left the wallet — it still
            // holds the wallet's leaves from a prior in-flight transfer. The Lightning HTLC was
            // NOT initiated from this attempt. Mark the row Failed so it can't masquerade as a
            // successful idempotency hit on the next click (without this, EnsurePendingAsync
            // would return the same row with no SspRequestId and the wizard would render Done
            // with empty data, looking like success). Then rethrow the original
            // SparkConnectionException — the Send wizard's confirm handler catches it
            // specifically and abandons the wizard.
            logger.LogWarning(
                "Spark Lightning send hit AlreadyExists — coordinator already has state for these leaves; the Lightning payment was NOT initiated (wallet={WalletId}, hash={Hash}, msg={Msg})",
                entity.Id, paymentHash, scx.Message);
            await outgoingPayments.MarkFailedAsync(row.Id,
                $"Coordinator rejected with AlreadyExists: {scx.Message}",
                DateTimeOffset.UtcNow, ct);
            throw;
        }
        catch (Exception ex)
        {
            // NSpark rejected the send. Mark our row Failed so it doesn't sit pending forever
            // (the background poller would otherwise leave it alone — it can't reconcile a row
            // that has no SspRequestId).
            await outgoingPayments.MarkFailedAsync(row.Id, ex.Message, DateTimeOffset.UtcNow, ct);
            throw;
        }
    }

    // -----------------------------------------------------------------------
    // History
    // -----------------------------------------------------------------------

    [HttpGet("stores/{storeId}/history")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> History(string storeId, long offset = 0, int take = 25, bool showInternal = false, CancellationToken ct = default)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        // Authoritative SO transfer page. Done first so a slow SO call surfaces visibly rather
        // than after the local queries succeed. Failure is non-fatal — the local-feed tabs still
        // work and the All tab shows the error inline.
        var transfers = await transferActivity.GetActivityPageAsync(
            entity!.Id, take: take, offset: offset, hideInternalSwaps: !showInternal, ct: ct);

        await using var dbctx = await dbContextFactory.CreateDbContextAsync(ct);
        var invoices = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(dbctx.LightningInvoices.AsNoTracking()
                .Where(i => i.WalletId == entity!.Id)
                .OrderByDescending(i => i.CreatedAt)
                .Take(100), ct);
        var outgoing = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(dbctx.OutgoingPayments.AsNoTracking()
                .Where(p => p.WalletId == entity!.Id)
                .OrderByDescending(p => p.CreatedAt)
                .Take(100), ct);
        var deposits = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(dbctx.WalletDeposits.AsNoTracking()
                .Where(d => d.WalletId == entity!.Id)
                .OrderByDescending(d => d.CreatedAt)
                .Take(100), ct);
        var withdrawals = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(dbctx.WalletWithdrawals.AsNoTracking()
                .Where(w => w.WalletId == entity!.Id)
                .OrderByDescending(w => w.CreatedAt)
                .Take(100), ct);

        var vm = new SparkHistoryViewModel
        {
            StoreId = storeId,
            Transfers = transfers,
            ShowInternalSwaps = showInternal,
            InvoiceRows = invoices.Select(SparkActivityRowFactory.FromInvoice).ToList(),
            InvoiceCount = invoices.Count,
            OutgoingPaymentRows = outgoing.Select(SparkActivityRowFactory.FromOutgoingPayment).ToList(),
            OutgoingPaymentCount = outgoing.Count,
            DepositRows = deposits.Select(d => SparkActivityRowFactory.FromDeposit(d, BtcNetwork)).ToList(),
            DepositCount = deposits.Count,
            WithdrawalRows = withdrawals.Select(w => SparkActivityRowFactory.FromWithdrawal(w, BtcNetwork)).ToList(),
            WithdrawalCount = withdrawals.Count,
        };
        return View(vm);
    }

    // -----------------------------------------------------------------------
    // Settings + seed reveal + remove
    // -----------------------------------------------------------------------

    [HttpGet("stores/{storeId}/settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Settings(string storeId, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        var canManagePrivateKeys = (await authorizationService.AuthorizeAsync(User, null,
            new PolicyRequirement(Policies.CanModifyServerSettings))).Succeeded;

        bool? privacyEnabled = null;
        string? privacyError = null;
        try
        {
            var wallet = await walletProvider.GetByWalletIdAsync(entity!.Id, ct);
            if (wallet is not null)
            {
                var settings = await wallet.GetWalletSettingsAsync(ct);
                privacyEnabled = settings.PrivateEnabled;
            }
        }
        catch (Exception ex)
        {
            privacyError = ex.Message;
            logger.LogWarning(ex, "Failed to read Spark wallet settings for {WalletId}", entity!.Id);
        }

        return View(new SparkSettingsViewModel
        {
            StoreId = storeId,
            WalletId = entity!.Id,
            Network = entity.Network.ToString(),
            SparkAddress = entity.SparkAddress,
            IdentityPublicKeyHex = entity.IdentityPublicKeyHex,
            Account = entity.Account,
            PrivacyEnabled = privacyEnabled,
            PrivacyError = privacyError,
            CanManagePrivateKeys = canManagePrivateKeys,
        });
    }

    [HttpPost("stores/{storeId}/settings/privacy")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SetPrivacy(string storeId, bool enabled, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        try
        {
            var wallet = await walletProvider.GetByWalletIdAsync(entity!.Id, ct);
            if (wallet is null) throw new SparkWalletNotConfiguredException(storeId);
            var updated = await wallet.SetPrivacyEnabledAsync(enabled, ct);
            TempData[WellKnownTempData.SuccessMessage] = updated.PrivateEnabled
                ? "Privacy mode enabled — wallet is hidden from public Spark explorers."
                : "Privacy mode disabled — wallet may appear in Spark explorers.";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to update privacy setting: {ex.Message}";
        }
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    /// <summary>
    /// Re-displays the wallet's recovery mnemonic on BTCPay's secure recovery-seed page.
    /// Uses <see cref="Policies.CanModifyStoreSettings"/> (NOT <c>CanModifyServerSettings</c>) —
    /// the server-settings policy is unscoped, so BTCPay's per-store middleware doesn't run for
    /// it and <c>HttpContext.GetStoreData()</c> throws <c>InvalidOperationException("StoreData is
    /// not set")</c>. The unhandled exception then gets the plugin auto-disabled. Matches Arkade's
    /// <c>ShowPrivateKey</c>: any user who can modify the store can reveal its seed.
    /// </summary>
    [HttpPost("stores/{storeId}/settings/reveal-seed")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> RevealSeed(string storeId, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        var mnemonic = walletService.DecryptMnemonic(entity!);
        return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
        {
            ReturnUrl = Url.Action(nameof(Settings), new { storeId }),
            IsStored = true,
            RequireConfirm = false,
            CryptoCode = "BTC",
            Mnemonic = mnemonic,
        });
    }

    /// <summary>
    /// Wipes the wallet for this store completely — mirrors Arkade's <c>ClearWallet</c>:
    /// <list type="number">
    ///   <item><description>If the store's BTC Lightning method is wired to Spark, clear it
    ///   (passing <c>null</c> so the config row is removed, not left in place with an empty
    ///   connection string — that empty record kept BTCPay treating LN as "configured but broken").</description></item>
    ///   <item><description>Evict the wallet from the in-memory cache so the event subscriber and
    ///   auto-claimer stop touching it.</description></item>
    ///   <item><description>Delete the wallet entity + every dependent row (LightningInvoices,
    ///   WalletDeposits, WalletWithdrawals, EventCursors) via <c>SparkWalletService.DeleteAsync</c>.</description></item>
    /// </list>
    /// After this the store has no Spark state at all and the sidebar will redirect to
    /// <c>InitialSetup</c>. Use <c>Policies.CanModifyStoreSettings</c> (not server settings) so the
    /// store owner can reset their own state without admin help — same as Arkade.
    /// </summary>
    [HttpPost("stores/{storeId}/settings/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> RemoveWallet(string storeId, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        var store = HttpContext.GetStoreData();
        if (store is null) return NotFound();

        // Clear the Lightning config if it currently uses Spark (matches Arkade's lenient prefix
        // check — exact-match on the wallet id is too brittle if anyone hand-edited the string).
        var existingLn = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(LightningPaymentMethodId, paymentMethodHandlers);
        var lnUsingSpark = existingLn?.ConnectionString?
            .StartsWith($"type={SparkLightningConnectionStringHandler.ConnectionStringType}", StringComparison.OrdinalIgnoreCase) == true;
        if (lnUsingSpark)
        {
            store.SetPaymentMethodConfig(paymentMethodHandlers[LightningPaymentMethodId], null);
            await storeRepository.UpdateStore(store);
        }

        walletProvider.Invalidate(entity!.Id);
        await walletService.DeleteAsync(entity.Id, ct);

        TempData[WellKnownTempData.SuccessMessage] = "Spark wallet removed. All wallet state cleared.";
        return RedirectToAction(nameof(InitialSetup), new { storeId });
    }

    // -----------------------------------------------------------------------
    // Enable / disable Lightning (used by the "Use Spark" tab on the LN setup page)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sets the store's Lightning payment method to use this wallet's Spark connection string,
    /// enables LNURL (Bech32, no LUD-12), and flips the store-blob flags BTCPay uses to surface
    /// Lightning on the checkout (mirrors Arkade's <c>EnableLightning</c>). Both GET and POST are
    /// accepted because the tab-head pill navigates via <c>location.href</c> (GET) while form
    /// submissions elsewhere would POST.
    /// </summary>
    // POST-only — this endpoint changes the store's Lightning routing. Accepting GET was a CSRF
    // risk: a cross-origin <img src=…enable-ln> from a logged-in admin's browser would silently
    // hijack the store's payment method. The "Use Spark" pill in SparkLNSetupTabhead.cshtml now
    // submits a hidden form instead of using location.href.
    [HttpPost("stores/{storeId}/enable-ln")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EnableLightning(string storeId, CancellationToken ct)
    {
        var (entity, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        var store = HttpContext.GetStoreData();
        if (store is null) return NotFound();

        logger.LogInformation("EnableLightning invoked for store {StoreId}, wallet {WalletId}", storeId, entity!.Id);

        // 1. Lightning method
        var connStr = $"type={SparkLightningConnectionStringHandler.ConnectionStringType};wallet-id={entity.Id}";
        store.SetPaymentMethodConfig(paymentMethodHandlers[LightningPaymentMethodId],
            new LightningPaymentMethodConfig { ConnectionString = connStr });

        // 2. LNURL method (mirrors Arkade so e.g. <BTCPay-instance>/.well-known/lnurlp/<store>
        //    works out of the box once LN is configured).
        var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
        if (paymentMethodHandlers.TryGetValue(lnurlPaymentMethodId, out var lnurlHandler))
        {
            store.SetPaymentMethodConfig(lnurlHandler, new LNURLPaymentMethodConfig
            {
                UseBech32Scheme = true,
                LUD12Enabled = false,
            });
        }

        // 3. Store blob flags: un-exclude the LN method so it shows up in checkout, and flip
        //    OnChainWithLnInvoiceFallback so the unified BTC/LN QR works.
        var blob = store.GetStoreBlob();
        blob.SetExcluded(LightningPaymentMethodId, false);
        blob.OnChainWithLnInvoiceFallback = true;
        store.SetStoreBlob(blob);

        await storeRepository.UpdateStore(store);

        TempData[WellKnownTempData.SuccessMessage] = "Lightning enabled — invoices and payouts route through Spark.";
        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    // POST-only — same CSRF rationale as EnableLightning above.
    [HttpPost("stores/{storeId}/disable-ln")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DisableLightning(string storeId, CancellationToken ct)
    {
        var (_, err) = await ResolveAsync(storeId, ct);
        if (err is not null) return err;

        var store = HttpContext.GetStoreData();
        if (store is null) return NotFound();

        store.SetPaymentMethodConfig(paymentMethodHandlers[LightningPaymentMethodId], null);
        await storeRepository.UpdateStore(store);
        TempData[WellKnownTempData.SuccessMessage] = "Lightning payment method disabled.";
        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<(SparkWalletEntity?, IActionResult?)> ResolveAsync(string storeId, CancellationToken ct)
    {
        var store = HttpContext.GetStoreData();
        if (store is null) return (null, NotFound());
        var entity = await walletService.GetByStoreAsync(storeId, ct);
        if (entity is null) return (null, RedirectToAction(nameof(InitialSetup), new { storeId }));
        return (entity, null);
    }

    private static bool LooksLikeBolt11(string s)
        => s.StartsWith("lnbc", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("lntb", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("lnbcrt", StringComparison.OrdinalIgnoreCase);

    private bool IsLightningConfigured()
    {
        var store = HttpContext.GetStoreData();
        if (store is null) return false;
        var ln = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(LightningPaymentMethodId, paymentMethodHandlers);
        return !string.IsNullOrEmpty(ln?.ConnectionString)
               && ln.ConnectionString.Contains($"type={SparkLightningConnectionStringHandler.ConnectionStringType}", StringComparison.OrdinalIgnoreCase);
    }
}
