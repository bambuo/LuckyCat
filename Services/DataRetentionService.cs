using System.Diagnostics;
using LuckyCat.Data;
using LuckyCat.Models;
using Microsoft.EntityFrameworkCore;

namespace LuckyCat.Services;

/// <summary>
/// 阶段/工具明细 90 天保留清理（LC-STD-03 §2.5）：每日本地 03:30 跑一次，同一事务内先删
/// <see cref="ToolInvocation"/>、再删 <see cref="AgentStageRun"/>，每表一条按 cutoff 的 DELETE 即可
/// 删完所有到期行；启动已过 03:30 不回补（首次排到次日 03:30），单次失败只记异常类型并在下一日程重试。
/// 模型未声明 HasOne/HasForeignKey，不存在「被外键引用而需跳过」的行；TraderLesson.StageRunId
/// 只是松散历史引用，不阻止明细清理，经验正文永久保留。
/// </summary>
public sealed class DataRetentionService : BackgroundService
{
    /// <summary>明细保留期：90 天（严格小于 cutoff 才删除，恰好 90 天保留）。</summary>
    internal static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(90);

    /// <summary>清理时刻：本地 03:30（LC-STD-03 §2.5）。</summary>
    private static readonly TimeSpan LocalCleanupTime = new(3, 30, 0);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTimeOffset> _clock;

    public DataRetentionService(IServiceScopeFactory scopeFactory) : this(scopeFactory, null)
    {
    }

    internal DataRetentionService(IServiceScopeFactory scopeFactory, Func<DateTimeOffset>? clock)
    {
        _scopeFactory = scopeFactory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _clock();
            var next = NextRunAt(now);

            // 只等到下一次本地 03:30：启动晚于 03:30 时 next 已是次日，不补跑当天。
            try
            {
                var delay = next - now;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunOnceAsync(_clock(), stoppingToken);
                // 运行完后再算下次：即使运行跨越 03:30，也不会在当天重复触发。
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 失败不终止循环：只记异常类型，下一日程继续；异常原文不进日志（LC-STD-02 §10）。
                Console.WriteLine($"[Retention] 清理失败：{ex.GetType().Name}，跳过本轮，下次本地 03:30 重试");
            }
        }
    }

    /// <summary>执行一次清理（测试与手动补跑共用的缝）；返回各表删除行数与耗时。</summary>
    public async Task<RetentionResult> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var cutoff = now.UtcDateTime - RetentionPeriod;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 工具明细先于阶段明细；两条删除同属一个事务。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tools = await DeleteToolInvocationsAsync(db, cutoff, ct);
        var stages = await DeleteAgentStageRunsAsync(db, cutoff, ct);
        await transaction.CommitAsync(ct);

        stopwatch.Stop();
        Console.WriteLine(
            $"[Retention] 清理完成：工具明细 {tools} 条、阶段明细 {stages} 条，用时 {stopwatch.Elapsed.TotalSeconds:0.#}s（阈值 {cutoff:O}）");
        return new RetentionResult(tools, stages, stopwatch.Elapsed);
    }

    /// <summary>下一次本地 03:30；恰好 03:30 返回当前时刻（零等待执行一次）。</summary>
    internal static DateTimeOffset NextRunAt(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var candidate = localNow.Date.Add(LocalCleanupTime);
        if (candidate < localNow.DateTime) candidate = candidate.AddDays(1);

        // 夏令时跳过的本地时刻顺延一小时；歧义时刻按标准偏移落成 DateTimeOffset。
        if (TimeZoneInfo.Local.IsInvalidTime(candidate)) candidate = candidate.AddHours(1);
        return new DateTimeOffset(candidate, TimeZoneInfo.Local.GetUtcOffset(candidate));
    }

    internal DateTimeOffset NextRunAt() => NextRunAt(_clock());

    // 不再分批：ExecuteDelete 不支持 Take/Skip，旧实现只能先物化 5,000 个主键再按主键删除，
    // 白多一次查询并抬高内存峰值。SQLite 的单条 DELETE 本就能按 cutoff 删完匹配行，
    // 因此两表各用一句 ExecuteDelete 直接交给数据库（仍包在同一事务内）。
    private static Task<int> DeleteToolInvocationsAsync(AppDbContext db, DateTime cutoff, CancellationToken ct) =>
        db.ToolInvocations
            .Where(invocation => invocation.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

    private static Task<int> DeleteAgentStageRunsAsync(AppDbContext db, DateTime cutoff, CancellationToken ct) =>
        // 无 FK 约束：TraderLesson.StageRunId 是松散历史引用，不阻塞删除。
        db.AgentStageRuns
            .Where(stage => stage.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
}

/// <summary>一次清理的结果：各表删除行数与耗时。</summary>
public sealed record RetentionResult(int ToolInvocationsDeleted, int AgentStageRunsDeleted, TimeSpan Elapsed);
