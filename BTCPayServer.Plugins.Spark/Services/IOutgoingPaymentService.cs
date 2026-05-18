using BTCPayServer.Plugins.Spark.Data;
using BTCPayServer.Plugins.Spark.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// Persistence layer for outgoing Lightning payments tracked by <see cref="SparkOutgoingPaymentEntity"/>.
/// Implements idempotent create (by <c>WalletId</c> + <c>PaymentHash</c>) so retries of
/// <c>SparkLightningClient.Pay</c> never double-send.
/// </summary>
public interface IOutgoingPaymentService
{
    /// <summary>
    /// Look up an existing record by <c>(WalletId, PaymentHash)</c>. Returns null if none.
    /// </summary>
    Task<SparkOutgoingPaymentEntity?> GetByPaymentHashAsync(string walletId, string paymentHash, CancellationToken ct = default);

    Task<SparkOutgoingPaymentEntity?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Atomically insert a new pending record if none exists for <c>(WalletId, PaymentHash)</c>;
    /// otherwise return the existing one. Caller must check <c>Status</c> before doing any SSP work.
    /// </summary>
    Task<(SparkOutgoingPaymentEntity Entity, bool Inserted)> EnsurePendingAsync(SparkOutgoingPaymentEntity entity, CancellationToken ct = default);

    /// <summary>Records the SSP request id (returned by NSpark) on the existing pending row.</summary>
    Task AttachSspRequestIdAsync(string id, string sspRequestId, CancellationToken ct = default);

    /// <summary>Idempotent transition Pending → Succeeded. Returns true if this call did the transition.</summary>
    Task<bool> MarkSucceededAsync(string id, string? preimage, long? actualFeeSats, DateTimeOffset completedAt, CancellationToken ct = default);

    /// <summary>Idempotent transition Pending → Failed. Returns true if this call did the transition.</summary>
    Task<bool> MarkFailedAsync(string id, string error, DateTimeOffset completedAt, CancellationToken ct = default);

    Task RecordPollAttemptAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<SparkOutgoingPaymentEntity>> ListAsync(string walletId, int take = 100, CancellationToken ct = default);

    Task<IReadOnlyList<SparkOutgoingPaymentEntity>> ListPendingForPollAsync(CancellationToken ct = default);
}

public class OutgoingPaymentService(IDbContextFactory<SparkPluginDbContext> dbContextFactory) : IOutgoingPaymentService
{
    public async Task<SparkOutgoingPaymentEntity?> GetByPaymentHashAsync(string walletId, string paymentHash, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.OutgoingPayments.AsNoTracking()
            .Where(p => p.WalletId == walletId && p.PaymentHash == paymentHash)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<SparkOutgoingPaymentEntity?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.OutgoingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<(SparkOutgoingPaymentEntity Entity, bool Inserted)> EnsurePendingAsync(SparkOutgoingPaymentEntity entity, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var existing = await ctx.OutgoingPayments
            .FirstOrDefaultAsync(p => p.WalletId == entity.WalletId && p.PaymentHash == entity.PaymentHash, ct);
        if (existing is not null)
            return (existing, false);

        ctx.OutgoingPayments.Add(entity);
        try
        {
            await ctx.SaveChangesAsync(ct);
            return (entity, true);
        }
        catch (DbUpdateException)
        {
            // Race: another thread inserted the same (WalletId, PaymentHash) concurrently.
            // The unique index ensures only one survives — reload and treat as not-inserted.
            ctx.Entry(entity).State = EntityState.Detached;
            await using var ctx2 = await dbContextFactory.CreateDbContextAsync(ct);
            var winner = await ctx2.OutgoingPayments.AsNoTracking()
                .FirstAsync(p => p.WalletId == entity.WalletId && p.PaymentHash == entity.PaymentHash, ct);
            return (winner, false);
        }
    }

    public async Task AttachSspRequestIdAsync(string id, string sspRequestId, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.OutgoingPayments.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return;
        entity.SspRequestId = sspRequestId;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> MarkSucceededAsync(string id, string? preimage, long? actualFeeSats, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.OutgoingPayments.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null || entity.Status != SparkOutgoingPaymentStatus.Pending) return false;
        entity.Status = SparkOutgoingPaymentStatus.Succeeded;
        entity.Preimage = preimage ?? entity.Preimage;
        entity.ActualFeeSats = actualFeeSats ?? entity.ActualFeeSats;
        entity.CompletedAt = completedAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.LastError = null;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> MarkFailedAsync(string id, string error, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.OutgoingPayments.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null || entity.Status != SparkOutgoingPaymentStatus.Pending) return false;
        entity.Status = SparkOutgoingPaymentStatus.Failed;
        entity.LastError = error.Length > 1000 ? error[..1000] : error;
        entity.CompletedAt = completedAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task RecordPollAttemptAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.OutgoingPayments.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return;
        entity.LastPolledAt = DateTimeOffset.UtcNow;
        entity.PollAttempt++;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SparkOutgoingPaymentEntity>> ListAsync(string walletId, int take = 100, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.OutgoingPayments.AsNoTracking()
            .Where(p => p.WalletId == walletId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SparkOutgoingPaymentEntity>> ListPendingForPollAsync(CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.OutgoingPayments.AsNoTracking()
            .Where(p => p.Status == SparkOutgoingPaymentStatus.Pending)
            .ToListAsync(ct);
    }
}
