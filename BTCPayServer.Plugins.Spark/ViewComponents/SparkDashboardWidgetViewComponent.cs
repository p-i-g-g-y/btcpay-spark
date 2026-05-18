using BTCPayServer;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Spark.Data;
using BTCPayServer.Plugins.Spark.Helpers;
using BTCPayServer.Plugins.Spark.Models;
using BTCPayServer.Plugins.Spark.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSpark.Services;

namespace BTCPayServer.Plugins.Spark.ViewComponents;

/// <summary>
/// Single combined Spark dashboard widget: balance, primary actions, and the four most recent
/// activity rows from any feed. Replaces the old split between this widget and
/// <c>SparkActivityDashboardWidgetViewComponent</c>, which is now intentionally a no-op so the
/// store dashboard isn't littered with two half-empty Spark cards.
/// </summary>
public class SparkDashboardWidgetViewComponent(
    SparkWalletService walletService,
    IStoreSparkWalletProvider walletProvider,
    BTCPayNetworkProvider networkProvider,
    IDbContextFactory<SparkPluginDbContext> dbContextFactory) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(StoreDashboardViewModel dashboardModel)
    {
        var ct = HttpContext.RequestAborted;
        var vm = new SparkDashboardWidgetViewModel { StoreId = dashboardModel?.StoreId ?? "" };

        if (string.IsNullOrEmpty(vm.StoreId)) return View(vm);

        var entity = await walletService.GetByStoreAsync(vm.StoreId, ct);
        if (entity is null) return View(vm);

        vm.Configured = true;
        vm.Network = entity.Network.ToString();

        try
        {
            var wallet = await walletProvider.GetByWalletIdAsync(entity.Id, ct);
            if (wallet is not null)
            {
                var bal = await wallet.GetBalanceAsync(ct);
                vm.Balances = new SparkBalancesViewModel
                {
                    AvailableSats = bal.SatsBalance.Available,
                    OwnedSats = bal.SatsBalance.Owned,
                    IncomingSats = bal.SatsBalance.Incoming,
                    LeafCount = bal.Leaves.Count,
                };
            }
        }
        catch (Exception ex)
        {
            vm.Error = ex.Message;
        }

        vm.RecentActivity = await LoadRecentActivityAsync(entity.Id, ct);
        return View(vm);
    }

    /// <summary>
    /// Pulls the top 4 entries (newest first) across invoices, outgoing payments, deposits, and
    /// withdrawals — small enough to fit comfortably under the balance, broad enough to give a
    /// real "what just happened" signal at a glance. Uses the same projection factory as the
    /// Overview / History pages so rows look identical.
    /// </summary>
    private async Task<IReadOnlyList<SparkActivityRow>> LoadRecentActivityAsync(string walletId, CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var network = networkProvider.BTC.NBitcoinNetwork;

        var invoices = await ctx.LightningInvoices.AsNoTracking()
            .Where(i => i.WalletId == walletId)
            .OrderByDescending(i => i.CreatedAt).Take(4).ToListAsync(ct);
        var payments = await ctx.OutgoingPayments.AsNoTracking()
            .Where(p => p.WalletId == walletId)
            .OrderByDescending(p => p.CreatedAt).Take(4).ToListAsync(ct);
        var deposits = await ctx.WalletDeposits.AsNoTracking()
            .Where(d => d.WalletId == walletId)
            .OrderByDescending(d => d.CreatedAt).Take(4).ToListAsync(ct);
        var withdrawals = await ctx.WalletWithdrawals.AsNoTracking()
            .Where(w => w.WalletId == walletId)
            .OrderByDescending(w => w.CreatedAt).Take(4).ToListAsync(ct);

        var merged = invoices.Select(SparkActivityRowFactory.FromInvoice)
            .Concat(payments.Select(SparkActivityRowFactory.FromOutgoingPayment))
            .Concat(deposits.Select(d => SparkActivityRowFactory.FromDeposit(d, network)))
            .Concat(withdrawals.Select(w => SparkActivityRowFactory.FromWithdrawal(w, network)));

        return merged.OrderByDescending(r => r.At).Take(4).ToList();
    }
}
