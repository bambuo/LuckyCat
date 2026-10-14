using LuckyCat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LuckyCat.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ExchangeAccount> ExchangeAccounts => Set<ExchangeAccount>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<DecisionRecord> DecisionRecords => Set<DecisionRecord>();
    public DbSet<Trader> Traders => Set<Trader>();
    public DbSet<AccountEquitySample> AccountEquitySamples => Set<AccountEquitySample>();
    public DbSet<TraderOrder> TraderOrders => Set<TraderOrder>();
    public DbSet<AccountPositionSnapshot> AccountPositionSnapshots => Set<AccountPositionSnapshot>();
    public DbSet<PositionHistory> PositionHistories => Set<PositionHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite 不保存 DateTimeKind；物化时恢复 Utc，保证 JSON 序列化带 Z 后缀
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        modelBuilder.Entity<Provider>(entity =>
        {
            entity.Property(p => p.CreatedAt).HasConversion(utcConverter);
            entity.Property(p => p.UpdatedAt).HasConversion(utcConverter);
            entity.Property(p => p.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.BaseUrl).HasMaxLength(500);
            entity.Property(p => p.ApiKey).HasMaxLength(500);
            entity.Property(p => p.Model).HasMaxLength(200);
            entity.Property(p => p.ApiVersion).HasMaxLength(50);
            entity.Property(p => p.ThinkingMode).HasMaxLength(16);
        });

        modelBuilder.Entity<ExchangeAccount>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasConversion(utcConverter);
            entity.Property(e => e.UpdatedAt).HasConversion(utcConverter);
            entity.Property(e => e.ExchangeType).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.AccountName).HasMaxLength(100);
            entity.Property(e => e.ApiKey).HasMaxLength(500);
            entity.Property(e => e.ApiSecret).HasMaxLength(500);
            entity.Property(e => e.Passphrase).HasMaxLength(500);
        });

        modelBuilder.Entity<Strategy>(entity =>
        {
            entity.Property(s => s.CreatedAt).HasConversion(utcConverter);
            entity.Property(s => s.UpdatedAt).HasConversion(utcConverter);
            entity.Property(s => s.Name).HasMaxLength(100);
            entity.Property(s => s.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<DecisionRecord>(entity =>
        {
            entity.Property(r => r.CreatedAt).HasConversion(utcConverter);
            entity.Property(r => r.ProviderName).HasMaxLength(100);
            entity.Property(r => r.Model).HasMaxLength(200);
            entity.Property(r => r.Symbol).HasMaxLength(50);
            entity.Property(r => r.Action).HasMaxLength(32);
            entity.Property(r => r.Reasoning).HasMaxLength(2000);
            entity.HasIndex(r => r.StrategyId);
            entity.HasIndex(r => r.TraderId);
        });

        modelBuilder.Entity<Trader>(entity =>
        {
            entity.Property(t => t.CreatedAt).HasConversion(utcConverter);
            entity.Property(t => t.UpdatedAt).HasConversion(utcConverter);
            entity.Property(t => t.StartedAt).HasConversion(utcConverter);
            entity.Property(t => t.LastCycleAt).HasConversion(utcConverter);
            entity.Property(t => t.Name).HasMaxLength(100);
            entity.Property(t => t.ExecutionMode).HasMaxLength(8);
            entity.Property(t => t.LastCycleStatus).HasMaxLength(16);
        });

        modelBuilder.Entity<AccountEquitySample>(entity =>
        {
            entity.Property(s => s.At).HasConversion(utcConverter);
            entity.HasIndex(s => s.ExchangeAccountId);
        });

        modelBuilder.Entity<TraderOrder>(entity =>
        {
            entity.Property(o => o.At).HasConversion(utcConverter);
            entity.Property(o => o.Symbol).HasMaxLength(50);
            entity.Property(o => o.Action).HasMaxLength(16);
            entity.Property(o => o.Note).HasMaxLength(500);
            entity.Property(o => o.Error).HasMaxLength(500);
            entity.HasIndex(o => o.TraderId);
        });

        modelBuilder.Entity<AccountPositionSnapshot>(entity =>
        {
            entity.Property(s => s.At).HasConversion(utcConverter);
            entity.Property(s => s.Symbol).HasMaxLength(50);
            entity.Property(s => s.Side).HasMaxLength(16);
            entity.HasIndex(s => s.ExchangeAccountId);
        });

        modelBuilder.Entity<PositionHistory>(entity =>
        {
            entity.Property(p => p.OpenTime).HasConversion(utcConverter);
            entity.Property(p => p.CloseTime).HasConversion(utcConverter);
            entity.Property(p => p.SyncedAt).HasConversion(utcConverter);
            entity.Property(p => p.DedupeKey).HasMaxLength(400);
            entity.Property(p => p.Source).HasMaxLength(32);
            entity.Property(p => p.Symbol).HasMaxLength(50);
            entity.Property(p => p.Side).HasMaxLength(16);
            entity.HasIndex(p => p.DedupeKey).IsUnique();
            entity.HasIndex(p => p.ExchangeAccountId);
        });
    }
}
