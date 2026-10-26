using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LuckyCat.Tests;

/// <summary>
/// LC-STD-03 §2.5 数据保留：阶段/工具明细 90 天、每日本地 03:30 清理；订单/提案/裁决/经验永久保留。
/// 全部使用内存 SQLite，不访问真实数据库（LC-STD-05 §2）。
/// </summary>
public class DataRetentionTests
{
    // 固定时钟：所有被清理行的时刻从它派生，不读当前时间（LC-STD-05 §1.5）。
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Old_details_are_deleted_recent_and_exact_cutoff_rows_kept()
    {
        await using var env = await RetentionEnv.CreateAsync();
        var cutoff = Now.UtcDateTime.AddDays(-90);
        // 91 天前（now - 91d）删除；89 天前保留；恰好在 cutoff 的保留——判据是严格小于。
        env.SeedStage("stage-old", cutoff.AddDays(-1));
        env.SeedStage("stage-recent", cutoff.AddDays(1));
        env.SeedStage("stage-cutoff", cutoff);
        env.SeedTool("stage-old", cutoff.AddDays(-1));
        env.SeedTool("stage-recent", cutoff.AddDays(1));
        env.SeedTool("stage-cutoff", cutoff);
        await env.Db.SaveChangesAsync();

        var result = await env.Service.RunOnceAsync(Now, default);

        Assert.Equal(1, result.ToolInvocationsDeleted);
        Assert.Equal(1, result.AgentStageRunsDeleted);
        Assert.False(await env.Db.AgentStageRuns.AnyAsync(r => r.Id == "stage-old"));
        Assert.True(await env.Db.AgentStageRuns.AnyAsync(r => r.Id == "stage-recent"));
        // 恰好 now - 90d：< cutoff 不成立，必须保留（边界语义）。
        Assert.True(await env.Db.AgentStageRuns.AnyAsync(r => r.Id == "stage-cutoff"));
        Assert.False(await env.Db.ToolInvocations.AnyAsync(t => t.StageRunId == "stage-old"));
        Assert.True(await env.Db.ToolInvocations.AnyAsync(t => t.StageRunId == "stage-recent"));
        Assert.True(await env.Db.ToolInvocations.AnyAsync(t => t.StageRunId == "stage-cutoff"));
    }

    [Fact]
    public async Task Lessons_survive_even_when_their_stage_run_is_deleted()
    {
        await using var env = await RetentionEnv.CreateAsync();
        var cutoff = Now.UtcDateTime.AddDays(-90);
        env.SeedStage("stage-old", cutoff.AddDays(-1));
        env.SeedTool("stage-old", cutoff.AddDays(-1));
        env.SeedLesson("stage-old", cutoff.AddDays(-1));
        await env.Db.SaveChangesAsync();

        await env.Service.RunOnceAsync(Now, default);

        // TraderLesson.stageRunId 是松散历史引用，不阻止明细清理；经验正文永久保留。
        Assert.False(await env.Db.AgentStageRuns.AnyAsync(r => r.Id == "stage-old"));
        var lesson = await env.Db.TraderLessons.SingleAsync();
        Assert.Equal("stage-old", lesson.StageRunId);
        Assert.Equal("守住止损就是纪律", lesson.Summary);
    }

    [Fact]
    public async Task Proposals_verdicts_and_orders_are_never_cleaned()
    {
        await using var env = await RetentionEnv.CreateAsync();
        var old = Now.UtcDateTime.AddDays(-365);
        env.SeedProposal("proposal-old", old);
        env.SeedVerdict("verdict-old", "proposal-old", old);
        env.SeedOrder(old);
        await env.Db.SaveChangesAsync();

        await env.Service.RunOnceAsync(Now, default);

        Assert.Equal(1, await env.Db.TradeProposals.CountAsync());
        Assert.Equal(1, await env.Db.RiskVerdicts.CountAsync());
        Assert.Equal(1, await env.Db.TraderOrders.CountAsync());
    }

    [Fact]
    public async Task Second_run_is_a_noop()
    {
        await using var env = await RetentionEnv.CreateAsync();
        var cutoff = Now.UtcDateTime.AddDays(-90);
        env.SeedStage("stage-old", cutoff.AddDays(-1));
        env.SeedTool("stage-old", cutoff.AddDays(-1));
        await env.Db.SaveChangesAsync();

        var first = await env.Service.RunOnceAsync(Now, default);
        var second = await env.Service.RunOnceAsync(Now, default);

        Assert.Equal(1, first.AgentStageRunsDeleted);
        Assert.Equal(0, second.AgentStageRunsDeleted);
        Assert.Equal(0, second.ToolInvocationsDeleted);
        Assert.Equal(0, await env.Db.AgentStageRuns.CountAsync());
        Assert.Equal(0, await env.Db.ToolInvocations.CountAsync());
    }

    [Fact]
    public async Task Single_delete_clears_more_than_the_old_batch_size()
    {
        await using var env = await RetentionEnv.CreateAsync();
        var cutoff = Now.UtcDateTime.AddDays(-90);
        // 旧实现每批 5,000 行循环；改为单条 ExecuteDelete 后必须一次清空超过旧批大小的存量。
        for (var i = 0; i < 5_001; i++)
        {
            env.SeedTool("stage-bulk", cutoff.AddDays(-1));
        }
        await env.Db.SaveChangesAsync();

        var result = await env.Service.RunOnceAsync(Now, default);

        Assert.Equal(5_001, result.ToolInvocationsDeleted);
        Assert.Equal(0, await env.Db.ToolInvocations.CountAsync());
    }

    [Fact]
    public async Task Scheduled_failure_is_logged_as_type_only_and_does_not_escape()
    {
        // 走已有时钟注入缝：本地 03:30 零等待触发一次，之后返回 03:31 让循环进入当日等待。
        var at330 = LocalTime(3, 30);
        var at331 = LocalTime(3, 31);
        var clockCalls = 0;
        var service = new DataRetentionService(new ThrowingScopeFactory(),
            () => clockCalls++ == 0 ? at330 : at331);

        var original = Console.Out;
        var log = new FailureLog();
        Console.SetOut(log);
        try
        {
            using var cts = new CancellationTokenSource();
            await service.StartAsync(cts.Token);
            // 首轮在后台线程执行：等失败日志实际写出后再停止，避免把取消当成「未运行」。
            await log.FailureWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }

        var text = log.ToString();
        Assert.Contains("[Retention] 清理失败：InvalidOperationException", text);
        // 只记录异常类型：异常原文（可能含内部路径）不得进日志。
        Assert.DoesNotContain("scope factory unavailable", text);
    }

    [Fact]
    public async Task Next_run_is_next_local_0330_without_startup_backfill()
    {
        await using var env = await RetentionEnv.CreateAsync();
        var day = new DateTime(2026, 10, 26);
        var offset = TimeZoneInfo.Local.GetUtcOffset(day.AddHours(3).AddMinutes(30));
        DateTimeOffset At(int hour, int minute) => new(day.AddHours(hour).AddMinutes(minute), offset);

        // 03:29 → 当天 03:30；03:31（启动已过点）→ 次日 03:30，不回补。
        Assert.Equal(day.AddHours(3).AddMinutes(30), LocalWallClock(env.WithClock(At(3, 29)).NextRunAt()));
        Assert.Equal(day.AddDays(1).AddHours(3).AddMinutes(30), LocalWallClock(env.WithClock(At(3, 31)).NextRunAt()));
        // 恰好 03:30：零等待执行一次；下一次调度仍落在次日。
        Assert.Equal(day.AddHours(3).AddMinutes(30), LocalWallClock(env.WithClock(At(3, 30)).NextRunAt()));
        Assert.Equal(day.AddDays(1).AddHours(3).AddMinutes(30),
            LocalWallClock(env.WithClock(At(3, 30).AddSeconds(1)).NextRunAt()));
    }

    private static DateTime LocalWallClock(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime;

    private static DateTimeOffset LocalTime(int hour, int minute)
    {
        var local = new DateTime(2026, 10, 26, hour, minute, 0);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("scope factory unavailable: /secret/path");
    }

    /// <summary>收集控制台输出，并在清理失败日志写出时放行测试线程。</summary>
    private sealed class FailureLog : StringWriter
    {
        public TaskCompletionSource FailureWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains("[Retention] 清理失败", StringComparison.Ordinal) == true)
            {
                FailureWritten.TrySetResult();
            }
        }
    }

    private sealed class RetentionEnv : IAsyncDisposable
    {
        public required AppDbContext Db { get; init; }
        public required IServiceScopeFactory ScopeFactory { get; init; }
        public required DataRetentionService Service { get; init; }

        private SqliteConnection Connection { get; init; } = null!;

        public static async Task<RetentionEnv> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var services = new ServiceCollection();
            services.AddScoped(_ => new AppDbContext(options));
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            return new RetentionEnv
            {
                Db = db,
                ScopeFactory = scopeFactory,
                Service = new DataRetentionService(scopeFactory),
                Connection = connection,
            };
        }

        public DataRetentionService WithClock(DateTimeOffset fixedNow) =>
            new(ScopeFactory, () => fixedNow);

        public AgentStageRun SeedStage(string id, DateTime createdAt)
        {
            var stage = new AgentStageRun
            {
                Id = id, Role = "trader", Status = "completed",
                ProviderName = "脚本提供商", Model = "scripted",
                Instructions = "[]", TranscriptJson = "[]", CreatedAt = createdAt,
            };
            Db.AgentStageRuns.Add(stage);
            return stage;
        }

        public ToolInvocation SeedTool(string stageRunId, DateTime createdAt)
        {
            var invocation = new ToolInvocation
            {
                StageRunId = stageRunId, ToolName = "get_market_snapshot",
                ArgumentsJson = "{}", ResultJson = "{}", Success = true, CreatedAt = createdAt,
            };
            Db.ToolInvocations.Add(invocation);
            return invocation;
        }

        public TraderLesson SeedLesson(string stageRunId, DateTime createdAt)
        {
            var lesson = new TraderLesson
            {
                TraderId = 7, StageRunId = stageRunId, Summary = "守住止损就是纪律",
                StrengthsJson = "[]", MistakesJson = "[]", RulesJson = "[]", EvidenceJson = "[]",
                PeriodStart = createdAt.AddDays(-1), PeriodEnd = createdAt, CreatedAt = createdAt,
            };
            Db.TraderLessons.Add(lesson);
            return lesson;
        }

        public TradeProposal SeedProposal(string id, DateTime createdAt)
        {
            var proposal = new TradeProposal
            {
                Id = id, CycleId = "cycle-old", Action = "open", Thesis = "趋势跟随",
                RawJson = "{}", CreatedAt = createdAt,
            };
            Db.TradeProposals.Add(proposal);
            return proposal;
        }

        public RiskVerdict SeedVerdict(string id, string proposalId, DateTime createdAt)
        {
            var verdict = new RiskVerdict
            {
                Id = id, CycleId = "cycle-old", ProposalId = proposalId, Decision = "approved",
                ReasonsJson = "[]", RawJson = "{}", CreatedAt = createdAt,
            };
            Db.RiskVerdicts.Add(verdict);
            return verdict;
        }

        public TraderOrder SeedOrder(DateTime at)
        {
            var order = new TraderOrder { TraderId = 7, At = at, Action = "open", Status = "filled" };
            Db.TraderOrders.Add(order);
            return order;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
