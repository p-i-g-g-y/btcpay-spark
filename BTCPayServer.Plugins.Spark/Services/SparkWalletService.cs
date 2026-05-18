using BTCPayServer.Plugins.Spark.Data;
using BTCPayServer.Plugins.Spark.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSpark;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// CRUD for <see cref="SparkWalletEntity"/>. Encrypts mnemonic at write time, decrypts at read time
/// via <see cref="IWalletSecretProtector"/>. Always operates on the live DbContext; the in-memory
/// <see cref="SparkWallet"/> cache lives in <see cref="StoreSparkWalletProvider"/>.
/// </summary>
public class SparkWalletService(
    IDbContextFactory<SparkPluginDbContext> dbContextFactory,
    IWalletSecretProtector secretProtector,
    SparkConnection sparkConnection)
{
    /// <summary>Creates or updates a wallet for a store from a (possibly newly generated) mnemonic.</summary>
    public async Task<SparkWalletEntity> UpsertWalletAsync(
        string storeId,
        string mnemonic,
        string? passphrase = null,
        int? account = null,
        CancellationToken ct = default)
    {
        var effectiveAccount = account ?? SparkWalletFactory.DefaultAccount(sparkConnection.Options.Network);

        // Spin up a transient wallet to extract identity / spark address (don't keep it; the
        // provider will create its own cached instance on first use).
        var transient = sparkConnection.CreateWallet(mnemonic, effectiveAccount, passphrase);
        var identityHex = transient.IdentityPublicKeyHex;
        var sparkAddress = transient.GetSparkAddress();

        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.Wallets.FirstOrDefaultAsync(w => w.StoreId == storeId, ct);
        if (entity is null)
        {
            entity = new SparkWalletEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                StoreId = storeId,
                Network = sparkConnection.Options.Network,
                EncryptedMnemonic = secretProtector.Encrypt(mnemonic),
                EncryptedPassphrase = string.IsNullOrEmpty(passphrase) ? null : secretProtector.Encrypt(passphrase),
                Account = effectiveAccount,
                IdentityPublicKeyHex = identityHex,
                SparkAddress = sparkAddress,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            ctx.Wallets.Add(entity);
        }
        else
        {
            entity.EncryptedMnemonic = secretProtector.Encrypt(mnemonic);
            entity.EncryptedPassphrase = string.IsNullOrEmpty(passphrase) ? null : secretProtector.Encrypt(passphrase);
            entity.Account = effectiveAccount;
            entity.Network = sparkConnection.Options.Network;
            entity.IdentityPublicKeyHex = identityHex;
            entity.SparkAddress = sparkAddress;
        }

        await ctx.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<SparkWalletEntity?> GetByStoreAsync(string storeId, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.StoreId == storeId, ct);
    }

    public async Task<SparkWalletEntity?> GetByIdAsync(string walletId, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == walletId, ct);
    }

    public async Task<IReadOnlyList<SparkWalletEntity>> ListAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        return await ctx.Wallets.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>Decrypts the stored mnemonic. Restricted to controllers that already pass the seed-reveal authz check.</summary>
    public string DecryptMnemonic(SparkWalletEntity entity) => secretProtector.Decrypt(entity.EncryptedMnemonic);

    public string? DecryptPassphrase(SparkWalletEntity entity)
        => entity.EncryptedPassphrase is null ? null : secretProtector.Decrypt(entity.EncryptedPassphrase);

    /// <summary>Persists the wallet's static deposit address once it's been derived.</summary>
    public async Task SetStaticDepositAddressAsync(string walletId, string address, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);
        var entity = await ctx.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, ct);
        if (entity is null) return;
        entity.StaticDepositAddress = address;
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes the wallet entity AND all rows that reference it (Lightning invoices, on-chain
    /// deposits and withdrawals, the event cursor). Mirrors Arkade's <c>ClearWallet</c> intent:
    /// when the admin says "remove", there should be no leftover state across the plugin tables.
    /// </summary>
    public async Task DeleteAsync(string walletId, CancellationToken ct = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct);

        // Remove dependent rows first. There are no FK constraints between these tables (matching
        // BTCPay/Arkade's loose-FK convention so deletes can happen in any order without DB drift),
        // so we issue per-table deletes explicitly.
        await ctx.LightningInvoices.Where(i => i.WalletId == walletId).ExecuteDeleteAsync(ct);
        await ctx.WalletDeposits.Where(d => d.WalletId == walletId).ExecuteDeleteAsync(ct);
        await ctx.WalletWithdrawals.Where(w => w.WalletId == walletId).ExecuteDeleteAsync(ct);
        await ctx.EventCursors.Where(c => c.WalletId == walletId).ExecuteDeleteAsync(ct);

        var entity = await ctx.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, ct);
        if (entity is null) return;
        ctx.Wallets.Remove(entity);
        await ctx.SaveChangesAsync(ct);
    }
}
