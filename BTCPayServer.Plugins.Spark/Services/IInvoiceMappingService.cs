using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Spark.Data;
using BTCPayServer.Plugins.Spark.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// Bridges Spark-side Lightning invoices to BTCPay's <see cref="ILightningInvoiceListener"/> world.
/// Persists the mapping in <see cref="SparkLightningInvoiceEntity"/> and fans out "paid" events
/// via per-wallet <see cref="Channel{T}"/>s so the listener can wake up immediately.
/// </summary>
public interface IInvoiceMappingService
{
    Task<SparkLightningInvoiceEntity> UpsertAsync(SparkLightningInvoiceEntity entity, CancellationToken ct = default);

    Task<SparkLightningInvoiceEntity?> GetByRequestIdAsync(string requestId, CancellationToken ct = default);

    Task<SparkLightningInvoiceEntity?> GetByPaymentHashAsync(string walletId, string paymentHash, CancellationToken ct = default);

    Task<IReadOnlyList<SparkLightningInvoiceEntity>> ListAsync(
        string walletId,
        SparkLightningInvoiceStatus? status = null,
        bool includePast = false,
        int take = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<SparkLightningInvoiceEntity>> ListPendingForPollAsync(CancellationToken ct = default);

    /// <summary>Marks paid (idempotent). Returns true if this call did the transition.</summary>
    Task<bool> MarkPaidAsync(string requestId, DateTimeOffset paidAt, CancellationToken ct = default);

    Task RecordPollAttemptAsync(string requestId, CancellationToken ct = default);

    Task MarkExpiredAsync(string requestId, CancellationToken ct = default);

    /// <summary>Acquires (or creates) the shared paid-invoice channel for a wallet. Reference-counted.</summary>
    ChannelReader<LightningInvoice> AcquireListener(string walletId, out IDisposable lease);

    /// <summary>Writes a paid invoice notification to every listener for that wallet.</summary>
    void Notify(string walletId, LightningInvoice invoice);
}

public class InvoiceMappingService(IDbContextFactory<SparkPluginDbContext> dbContextFactory) : IInvoiceMappingService
{
    private sealed class ListenerSlot
    {
        public Channel<LightningInvoice> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<LightningInvoice>();
        public int RefCount;
    }

    private readonly ConcurrentDictionary<string, ListenerSlot> _slots = new();

    public async Task<SparkLightningInvoiceEntity> UpsertAsync(SparkLightningInvoiceEntity entity, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var existing = await ctx.LightningInvoices.FirstOrDefaultAsync(i => i.RequestId == entity.RequestId, ct);
        if (existing is null)
        {
            ctx.LightningInvoices.Add(entity);
        }
        else
        {
            existing.WalletId = entity.WalletId;
            existing.BtcPayInvoiceId = entity.BtcPayInvoiceId ?? existing.BtcPayInvoiceId;
            existing.PaymentHash = entity.PaymentHash;
            existing.Bolt11 = entity.Bolt11;
            existing.AmountSats = entity.AmountSats;
            existing.Memo = entity.Memo ?? existing.Memo;
            existing.ExpiresAt = entity.ExpiresAt;
            existing.MetadataJson = entity.MetadataJson ?? existing.MetadataJson;
        }
        await ctx.SaveChangesAsync(ct);
        return existing ?? entity;
    }

    public async Task<SparkLightningInvoiceEntity?> GetByRequestIdAsync(string requestId, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.LightningInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.RequestId == requestId, ct);
    }

    public async Task<SparkLightningInvoiceEntity?> GetByPaymentHashAsync(string walletId, string paymentHash, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.LightningInvoices
            .AsNoTracking()
            .Where(i => i.WalletId == walletId && i.PaymentHash == paymentHash)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<SparkLightningInvoiceEntity>> ListAsync(
        string walletId,
        SparkLightningInvoiceStatus? status = null,
        bool includePast = false,
        int take = 100,
        CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        IQueryable<SparkLightningInvoiceEntity> q = ctx.LightningInvoices.AsNoTracking()
            .Where(i => i.WalletId == walletId);
        if (status is { } s) q = q.Where(i => i.Status == s);
        if (!includePast) q = q.Where(i => i.Status != SparkLightningInvoiceStatus.Expired);
        return await q.OrderByDescending(i => i.CreatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SparkLightningInvoiceEntity>> ListPendingForPollAsync(CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.LightningInvoices.AsNoTracking()
            .Where(i => i.Status == SparkLightningInvoiceStatus.Pending && i.ExpiresAt > DateTimeOffset.UtcNow)
            .ToListAsync(ct);
    }

    public async Task<bool> MarkPaidAsync(string requestId, DateTimeOffset paidAt, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.LightningInvoices.FirstOrDefaultAsync(i => i.RequestId == requestId, ct);
        if (entity is null || entity.Status != SparkLightningInvoiceStatus.Pending) return false;
        entity.Status = SparkLightningInvoiceStatus.Paid;
        entity.PaidAt = paidAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task RecordPollAttemptAsync(string requestId, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.LightningInvoices.FirstOrDefaultAsync(i => i.RequestId == requestId, ct);
        if (entity is null) return;
        entity.LastPolledAt = DateTimeOffset.UtcNow;
        entity.PollAttempt++;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task MarkExpiredAsync(string requestId, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.LightningInvoices.FirstOrDefaultAsync(i => i.RequestId == requestId, ct);
        if (entity is null || entity.Status != SparkLightningInvoiceStatus.Pending) return;
        entity.Status = SparkLightningInvoiceStatus.Expired;
        await ctx.SaveChangesAsync(ct);
    }

    public ChannelReader<LightningInvoice> AcquireListener(string walletId, out IDisposable lease)
    {
        var slot = _slots.GetOrAdd(walletId, _ => new ListenerSlot());
        Interlocked.Increment(ref slot.RefCount);
        lease = new Lease(this, walletId, slot);
        return slot.Channel.Reader;
    }

    public void Notify(string walletId, LightningInvoice invoice)
    {
        if (_slots.TryGetValue(walletId, out var slot))
        {
            slot.Channel.Writer.TryWrite(invoice);
        }
    }

    private sealed class Lease(InvoiceMappingService owner, string walletId, ListenerSlot slot) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref slot.RefCount) <= 0)
            {
                // Last listener went away: shut down the channel and remove the slot so a future
                // Acquire creates a fresh one.
                if (owner._slots.TryRemove(walletId, out var removed) && ReferenceEquals(removed, slot))
                {
                    slot.Channel.Writer.TryComplete();
                }
            }
        }
    }
}
