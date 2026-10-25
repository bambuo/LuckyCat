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
    public DbSet<TradingCycle> TradingCycles => Set<TradingCycle>();
    public DbSet<AgentStageRun> AgentStageRuns => Set<AgentStageRun>();
    public DbSet<ToolInvocation> ToolInvocations => Set<ToolInvocation>();
    public DbSet<TradeProposal> TradeProposals => Set<TradeProposal>();
    public DbSet<RiskVerdict> RiskVerdicts => Set<RiskVerdict>();
    public DbSet<TraderLesson> TraderLessons => Set<TraderLesson>();

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
            entity.Property(p => p.ToolCallingVerifiedAt).HasConversion(utcConverter);
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
            entity.Property(o => o.CycleId).HasMaxLength(26);
            entity.Property(o => o.ProposalId).HasMaxLength(26);
            entity.Property(o => o.VerdictId).HasMaxLength(26);
            entity.Property(o => o.ClientOrderId).HasMaxLength(64);
            entity.Property(o => o.Side).HasMaxLength(16);
            entity.Property(o => o.Status).HasMaxLength(16);
            entity.Property(o => o.ExchangeOrderId).HasMaxLength(100);
            entity.HasIndex(o => o.TraderId);
            entity.HasIndex(o => o.CycleId);
            entity.HasIndex(o => new { o.CycleId, o.ClientOrderId }).IsUnique();
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

        modelBuilder.Entity<TradingCycle>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).HasMaxLength(26);
            entity.Property(c => c.Mode).HasMaxLength(8);
            entity.Property(c => c.Trigger).HasMaxLength(16);
            entity.Property(c => c.Status).HasMaxLength(16);
            entity.Property(c => c.StartedAt).HasConversion(utcConverter);
            entity.Property(c => c.CompletedAt).HasConversion(utcConverter);
            entity.Property(c => c.ErrorCode).HasMaxLength(64);
            entity.Property(c => c.Error).HasMaxLength(1000);
            entity.Property(c => c.MemoryFingerprint).HasMaxLength(64);
            entity.HasIndex(c => new { c.TraderId, c.StartedAt });
            entity.HasIndex(c => c.Status);
            entity.HasIndex(c => new { c.StrategyId, c.StartedAt });
        });

        modelBuilder.Entity<AgentStageRun>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasMaxLength(26);
            entity.Property(r => r.CycleId).HasMaxLength(26);
            entity.Property(r => r.Role).HasMaxLength(16);
            entity.Property(r => r.Status).HasMaxLength(16);
            entity.Property(r => r.ProviderName).HasMaxLength(100);
            entity.Property(r => r.Model).HasMaxLength(200);
            entity.Property(r => r.Reasoning).HasMaxLength(2000);
            entity.Property(r => r.Error).HasMaxLength(1000);
            entity.Property(r => r.CreatedAt).HasConversion(utcConverter);
            entity.HasIndex(r => new { r.CycleId, r.Role });
            entity.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<ToolInvocation>(entity =>
        {
            entity.Property(i => i.CycleId).HasMaxLength(26);
            entity.Property(i => i.StageRunId).HasMaxLength(26);
            entity.Property(i => i.ToolName).HasMaxLength(64);
            entity.Property(i => i.ErrorCode).HasMaxLength(64);
            entity.Property(i => i.CreatedAt).HasConversion(utcConverter);
            entity.HasIndex(i => i.StageRunId);
            entity.HasIndex(i => i.CycleId);
            entity.HasIndex(i => i.CreatedAt);
        });

        modelBuilder.Entity<TradeProposal>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).HasMaxLength(26);
            entity.Property(p => p.CycleId).HasMaxLength(26);
            entity.Property(p => p.Action).HasMaxLength(16);
            entity.Property(p => p.Symbol).HasMaxLength(50);
            entity.Property(p => p.Side).HasMaxLength(16);
            entity.Property(p => p.Thesis).HasMaxLength(2000);
            entity.Property(p => p.Invalidation).HasMaxLength(1000);
            entity.Property(p => p.CreatedAt).HasConversion(utcConverter);
            entity.HasIndex(p => p.CycleId);
            entity.HasIndex(p => new { p.TraderId, p.CreatedAt });
            entity.HasIndex(p => new { p.Symbol, p.CreatedAt });
        });

        modelBuilder.Entity<RiskVerdict>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.Property(v => v.Id).HasMaxLength(26);
            entity.Property(v => v.CycleId).HasMaxLength(26);
            entity.Property(v => v.ProposalId).HasMaxLength(26);
            entity.Property(v => v.Decision).HasMaxLength(16);
            entity.Property(v => v.ValidUntilUtc).HasConversion(utcConverter);
            entity.Property(v => v.CreatedAt).HasConversion(utcConverter);
            entity.HasIndex(v => v.ProposalId).IsUnique();
            entity.HasIndex(v => v.CycleId);
        });

        modelBuilder.Entity<TraderLesson>(entity =>
        {
            entity.Property(l => l.StageRunId).HasMaxLength(26);
            entity.Property(l => l.Summary).HasMaxLength(2000);
            entity.Property(l => l.PeriodStart).HasConversion(utcConverter);
            entity.Property(l => l.PeriodEnd).HasConversion(utcConverter);
            entity.Property(l => l.CreatedAt).HasConversion(utcConverter);
            entity.HasIndex(l => new { l.TraderId, l.CreatedAt });
        });
    }
}
