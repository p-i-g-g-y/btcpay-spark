using System.Collections.Concurrent;
using BTCPayServer.Plugins.Spark.Data.Entities;
using NSpark;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// Hands out cached <see cref="SparkWallet"/> instances per BTCPay store. Decrypts the mnemonic
/// on first use; subsequent calls return the same instance (Spark calls are stateful — auth
/// tokens, gRPC channel pool, etc., live on the connection but per-wallet identity caches live
/// here).
/// </summary>
public interface IStoreSparkWalletProvider
{
    Task<SparkWallet?> GetByStoreAsync(string storeId, CancellationToken ct = default);
    Task<SparkWallet?> GetByWalletIdAsync(string walletId, CancellationToken ct = default);

    event EventHandler<SparkWalletEntity>? WalletReady;
    event EventHandler<string>? WalletEvicted;

    Task<IReadOnlyList<SparkWalletEntity>> ReadyWalletsAsync(CancellationToken ct = default);

    void Invalidate(string walletId);
}

public class StoreSparkWalletProvider(
    SparkWalletService walletService,
    SparkConnection connection) : IStoreSparkWalletProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<SparkWallet>>> _walletCache = new();
    private readonly ConcurrentDictionary<string, string> _storeToWalletId = new();

    public event EventHandler<SparkWalletEntity>? WalletReady;
    public event EventHandler<string>? WalletEvicted;

    public async Task<SparkWallet?> GetByStoreAsync(string storeId, CancellationToken ct = default)
    {
        var entity = await walletService.GetByStoreAsync(storeId, ct);
        if (entity is null) return null;
        return await ResolveAsync(entity, ct);
    }

    public async Task<SparkWallet?> GetByWalletIdAsync(string walletId, CancellationToken ct = default)
    {
        var entity = await walletService.GetByIdAsync(walletId, ct);
        if (entity is null) return null;
        return await ResolveAsync(entity, ct);
    }

    public async Task<IReadOnlyList<SparkWalletEntity>> ReadyWalletsAsync(CancellationToken ct = default)
        => await walletService.ListAllAsync(ct);

    public void Invalidate(string walletId)
    {
        if (_walletCache.TryRemove(walletId, out _))
        {
            WalletEvicted?.Invoke(this, walletId);
        }
    }

    private Task<SparkWallet> ResolveAsync(SparkWalletEntity entity, CancellationToken ct)
    {
        _storeToWalletId[entity.StoreId] = entity.Id;
        var lazy = _walletCache.GetOrAdd(entity.Id, _ => new Lazy<Task<SparkWallet>>(() =>
        {
            var mnemonic = walletService.DecryptMnemonic(entity);
            var passphrase = walletService.DecryptPassphrase(entity);
            var wallet = connection.CreateWallet(mnemonic, entity.Account, passphrase);
            WalletReady?.Invoke(this, entity);
            return Task.FromResult(wallet);
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }
}
