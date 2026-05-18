using BTCPayServer.Plugins.Spark.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Spark.Data;

public class SparkPluginDbContext(DbContextOptions<SparkPluginDbContext> options) : DbContext(options)
{
    public const string SchemaName = "BTCPayServer.Plugins.Spark";

    public DbSet<SparkWalletEntity> Wallets { get; set; } = null!;
    public DbSet<SparkLightningInvoiceEntity> LightningInvoices { get; set; } = null!;
    public DbSet<SparkOutgoingPaymentEntity> OutgoingPayments { get; set; } = null!;
    public DbSet<SparkWalletDepositEntity> WalletDeposits { get; set; } = null!;
    public DbSet<SparkWalletWithdrawalEntity> WalletWithdrawals { get; set; } = null!;
    public DbSet<SparkEventCursorEntity> EventCursors { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<SparkWalletEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StoreId).IsUnique();
            e.HasIndex(x => x.IdentityPublicKeyHex);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SparkLightningInvoiceEntity>(e =>
        {
            e.HasKey(x => x.RequestId);
            e.HasIndex(x => x.BtcPayInvoiceId);
            e.HasIndex(x => x.PaymentHash);
            e.HasIndex(x => new { x.WalletId, x.Status });
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SparkOutgoingPaymentEntity>(e =>
        {
            e.HasKey(x => x.Id);
            // PaymentHash unique per wallet → idempotency for Pay() retries
            e.HasIndex(x => new { x.WalletId, x.PaymentHash }).IsUnique();
            e.HasIndex(x => new { x.WalletId, x.Status });
            e.HasIndex(x => x.BtcPayPayoutId);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SparkWalletDepositEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Address);
            e.HasIndex(x => new { x.WalletId, x.Status });
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SparkWalletWithdrawalEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WalletId, x.Status });
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SparkEventCursorEntity>(e =>
        {
            e.HasKey(x => x.WalletId);
        });
    }
}
