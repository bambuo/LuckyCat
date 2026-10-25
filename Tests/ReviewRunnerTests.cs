using System.Globalization;
using CryptoExchange.Net.SharedApis;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Memory;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace LuckyCat.Tests;

/// <summary>
/// Task 13：P5 复盘 Agent、每日调度与经验落库（LC-STD-01 §9 复盘任务表、§1.35、§10 复盘耗时、
/// §7 验收项 5「跳过/成功/失败三种路径均有测试」；LC-STD-04 §2 块 6 交易员专属、§3.5 复盘员指令；
/// LC-STD-03 §2.1 TraderLesson/AgentStageRun 字段与 §6 错误码）。
/// 全部走脚本化 <see cref="IChatClient"/> + 绑定账户的假 <see cref="IAccountReader"/> + 内存 SQLite，
/// 不访问真实模型与交易所（LC-STD-05 §2）。
/// </summary>
public class ReviewRunnerTests(ITestOutputHelper output)
{
    // —— §9 三条必测路径：跳过 / 成功 / 失败 ——

    [Fact]
    public async Task No_data_is_skipped_and_writes_no_lesson()
    {
        await using var env = await ReviewEnv.CreateAsync();

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("skipped", result.Status);
        // §9 跳过条件：窗口内「无已平仓交易且无周期」→ 不产出经验、不调用模型。
        Assert.Empty(env.Db.TraderLessons);
        Assert.Equal(0, env.Client.Calls);
        Assert.Equal("review_no_data", result.ErrorCode);
        // 跳过以 skipped 阶段行留痕（复盘页可查询），但不写经验（R-13-3）。
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer");
        Assert.Equal("skipped", stage.Status);
        Assert.Null(stage.CycleId);
        Assert.Equal(env.Trader.Id, stage.TraderId);
        Assert.Empty(await env.Db.ToolInvocations.Where(t => t.StageRunId == stage.Id).ToListAsync());
    }

    [Fact]
    public async Task Report_is_persisted_and_injected_next_cycle()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("completed", result.Status);
        var lesson = Assert.Single(env.Db.TraderLessons);
        Assert.Contains("复盘结论：守住止损就是纪律", lesson.Summary);

        // brief 第 2 条：这份经验必须进入下一轮周期的交易员提示词（最近 3 份、≤1,200 字）。
        var memory = new TradingMemoryContextProvider(env.Reader, env.Config);
        var blocks = await memory.BuildContextAsync(default);
        Assert.Contains("复盘结论：守住止损就是纪律", blocks.LessonsBlock);
        Assert.True(blocks.LessonsBlock!.Length <= 1200);
        output.WriteLine($"lesson id={lesson.Id} window={lesson.PeriodStart:O}~{lesson.PeriodEnd:O}");
    }

    [Fact]
    public async Task Model_failure_records_failed_stage_and_writes_no_lesson()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "throw");

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("failed", result.Status);
        Assert.Empty(env.Db.TraderLessons);
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer");
        Assert.Equal("failed", stage.Status);
        Assert.Null(stage.CycleId);
        Assert.Equal("stage_failed", result.ErrorCode);
        // R-13-7：上游异常原文只进日志；落库必须是稳定错误码 + 固定中文摘要。
        Assert.StartsWith("stage_failed：", stage.Error);
        Assert.DoesNotContain("上游炸了", stage.Error);
        Assert.DoesNotContain("invalid.test", stage.Error);
        Assert.DoesNotContain("sk-test", stage.Error);
    }

    [Fact]
    public async Task Failed_persist_still_lands_exactly_one_failed_stage_row()
    {
        // 钉死 AddStageRun 的幂等写（6cfccc2）：成功路径的落库在阶段行已 Add 未提交时抛错，
        // 宿主紧随其后的失败补写在同一个 ChangeTracker 里改写那份行，而不是再 Add 一份撞
        // EF 身份冲突——「记录失败」这件事本身绝不能丢：阶段行恰好 1 条、状态 failed、错误码稳定。
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, failFirstSave: true);

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("failed", result.Status);
        Assert.Equal("stage_failed", result.ErrorCode);
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer");
        Assert.Equal("failed", stage.Status);
        Assert.Null(stage.CycleId);
        Assert.StartsWith("stage_failed：", stage.Error);
        // 模拟的基础设施异常原文不入库（R-13-7）；行数 Single 已证明没有重复行。
        Assert.DoesNotContain("模拟落库失败", stage.Error);
        Assert.Equal(1, await env.Db.AgentStageRuns.CountAsync());
    }

    [Fact]
    public void Logged_exception_text_is_scrubbed_of_urls_and_credential_fragments()
    {
        // R-13-7 / LC-STD-04 §8：上游异常文本可能自带含密钥的 URL，进日志前必须脱敏。
        // 测试集没有 Console 捕获缝（宿主直接 Console.WriteLine），故直接钉死脱敏函数本身
        // ——两处日志站点（ReviewRunner/TradingCycleRunner 的 LogException）都只是这一行调用。
        var raw = new HttpRequestException(
            "上游 502 https://api.invalid.test/v1?key=sk-leakedvalue Authorization: Bearer leaked-token 收尾").ToString();

        var line = LogRedaction.Scrub(raw);

        Assert.DoesNotContain("invalid.test", line);
        Assert.DoesNotContain("sk-leakedvalue", line);
        Assert.DoesNotContain("leaked-token", line);
        Assert.Contains("[url]", line);
        Assert.Contains("上游 502", line); // 非敏感内容保留，脱敏不是打码整行
    }

    [Fact]
    public async Task Cycles_without_closed_trades_still_run_the_review()
    {
        // §9 跳过要两个「无」同时成立；只有周期也必须复盘。
        await using var env = await ReviewEnv.CreateAsync(cycles: 1);

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("completed", result.Status);
        Assert.Single(env.Db.TraderLessons);
    }

    [Fact]
    public async Task Closed_trades_outside_the_window_are_skipped()
    {
        // 窗口=上次成功复盘之后：45 天前平仓、10 天前已复盘 → 窗口内无数据。
        await using var env = await ReviewEnv.CreateAsync();
        env.SeedLesson(DateTime.UtcNow.AddDays(-10), "上一轮经验");
        env.Reader.AddClosedTrade(DateTime.UtcNow.AddDays(-45));

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("skipped", result.Status);
        Assert.Equal(1, await env.Db.TraderLessons.CountAsync());
        Assert.Equal(0, env.Client.Calls);
    }

    // —— 窗口口径：上次成功复盘之后、最多 30 天 ——

    [Fact]
    public async Task Window_starts_after_the_last_successful_review()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        var lastReview = DateTime.UtcNow.AddDays(-12);
        env.SeedLesson(lastReview, "上一轮经验");

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("completed", result.Status);
        var lesson = env.Db.TraderLessons.OrderByDescending(l => l.Id).First();
        Assert.True((lesson.PeriodStart - lastReview).Duration() < TimeSpan.FromMinutes(1),
            $"窗口起点应贴合上次复盘 {lastReview:O}，实际 {lesson.PeriodStart:O}");
        Assert.True(lesson.PeriodEnd > lesson.PeriodStart);
    }

    [Fact]
    public async Task Window_is_capped_at_configured_max_lookback_days()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.Config.Review.MaxLookbackDays = 7;
        env.WriteConfig();

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("completed", result.Status);
        var lesson = env.Db.TraderLessons.Single();
        var span = lesson.PeriodEnd - lesson.PeriodStart;
        Assert.True(span > TimeSpan.FromDays(6) && span <= TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1),
            $"窗口应被配置裁到 7 天，实际 {span.TotalDays:0.##} 天");
    }

    [Fact]
    public async Task Lookback_cap_never_exceeds_thirty_days()
    {
        // §9「最多 30 天」是硬上限：配置放大到 90 天也必须裁到 30 天。
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.Config.Review.MaxLookbackDays = 90;
        env.WriteConfig();

        await env.Runner.RunAsync(env.Trader, default);

        var lesson = env.Db.TraderLessons.Single();
        Assert.True((lesson.PeriodEnd - lesson.PeriodStart) <= TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
    }

    // —— 落库字段与 TraderLesson / AgentStageRun 契约（LC-STD-03 §2.1）——

    [Fact]
    public async Task Persisted_lesson_matches_the_review_report_fields()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);

        await env.Runner.RunAsync(env.Trader, default);

        var lesson = env.Db.TraderLessons.Single();
        Assert.Equal(env.Trader.Id, lesson.TraderId);
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer" && r.Status == "completed");
        Assert.Equal(stage.Id, lesson.StageRunId);
        Assert.Equal("复盘结论：守住止损就是纪律", lesson.Summary);
        Assert.Equal(["先等回踩再开仓", "手续费计入盈亏比"], ReadArray(lesson.StrengthsJson));
        Assert.Equal(["追高一次未设止损"], ReadArray(lesson.MistakesJson));
        Assert.Equal(["不开没有止损的仓位", "平仓后 24 小时内不复购同币"], ReadArray(lesson.RulesJson));
        Assert.Contains("01TESTORDER0000000000000A", ReadArray(lesson.EvidenceJson));

        // 阶段行：role=reviewer、cycleId=null、traderId 填写、Attempt 从 1 起、系统指令全文落库。
        Assert.Equal("reviewer", stage.Role);
        Assert.Null(stage.CycleId);
        Assert.Equal(env.Trader.Id, stage.TraderId);
        Assert.Equal(1, stage.Attempt);
        Assert.Equal(PromptLibrary.Build(AgentRole.Reviewer, env.Config), stage.Instructions);
        Assert.Contains("复盘结论：守住止损就是纪律", stage.OutputJson);
        Assert.True(stage.ElapsedMs >= 0);
    }

    [Fact]
    public async Task Lesson_fields_are_clamped_to_the_contract_limits()
    {
        // LC-STD-03 §2.1：summary ≤2,000 字；rules ≤10 条、每条 ≤200 字。
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "oversize");

        await env.Runner.RunAsync(env.Trader, default);

        var lesson = env.Db.TraderLessons.Single();
        Assert.True(lesson.Summary.Length <= 2000, $"summary {lesson.Summary.Length} 字超上限");
        var rules = ReadArray(lesson.RulesJson);
        Assert.True(rules.Count <= 10, $"rules {rules.Count} 条超上限");
        Assert.All(rules, rule => Assert.True(rule.Length <= 200, $"单条规则 {rule.Length} 字超上限"));
    }

    [Fact]
    public async Task Contract_violation_is_recorded_as_failure_not_a_lesson()
    {
        // 空摘要=输出契约不成立：记 stage_contract_violation（§6 既有码），不写经验。
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "empty");

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("failed", result.Status);
        Assert.Empty(env.Db.TraderLessons);
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer");
        Assert.Equal("failed", stage.Status);
        Assert.StartsWith("stage_contract_violation：", stage.Error);
    }

    // —— R-13-6：单次超时 120s、工具上限 10（§9 限制行）——

    [Fact]
    public void Review_run_advertises_the_standard_review_limits()
    {
        Assert.Equal(120, ReviewRunner.DefaultTimeoutSeconds);
        Assert.Equal(10, ReviewRunner.DefaultToolLimit);
    }

    [Fact]
    public async Task Timeout_is_recorded_as_failure_without_a_lesson()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "hang", timeoutSeconds: 1);

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("failed", result.Status);
        Assert.Empty(env.Db.TraderLessons);
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer");
        Assert.Equal("failed", stage.Status);
        // 超时经 CancellationToken 传播（LC-STD-01 §6 禁止 Task.WhenAny 遗留未取消任务）。
        Assert.StartsWith("stage_failed：", stage.Error);
        Assert.Contains("超时", stage.Error);
    }

    [Fact]
    public async Task Tool_cap_exhaustion_is_recorded_as_failure()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "tool_loop");

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("failed", result.Status);
        Assert.Equal("tool_budget_exceeded", result.ErrorCode);
        Assert.Empty(env.Db.TraderLessons);
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer");
        Assert.Equal("failed", stage.Status);
        Assert.StartsWith("tool_budget_exceeded：", stage.Error);
        // 第 11 次起被上限拒绝，且拒绝本身留有审计行。
        var refused = await env.Db.ToolInvocations
            .Where(t => t.StageRunId == stage.Id && t.ErrorCode == ToolErrors.ToolBudgetExceeded)
            .ToListAsync();
        Assert.NotEmpty(refused);
    }

    // —— R-13-8：复盘员不接记忆块（块 6 交易员专属）——

    [Fact]
    public void Reviewer_system_instructions_omit_the_memory_block()
    {
        var config = new StrategyConfig { RoleInstructions = { Trader = "只做人多仓" } };
        var instructions = PromptLibrary.Build(AgentRole.Reviewer, config);

        Assert.DoesNotContain("历史决策记忆", instructions);
        Assert.DoesNotContain("上下文提供器会注入最近 8 条", instructions);
        // 安全块收尾（LC-STD-04 §2）。
        Assert.EndsWith(PromptLibrary.SafetyBlockText.Trim(), instructions);
    }

    [Fact]
    public async Task Reviewer_model_request_carries_no_memory_injection()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);

        await env.Runner.RunAsync(env.Trader, default);

        Assert.NotEmpty(env.Client.CapturedMessages);
        var systemTexts = env.Client.CapturedMessages
            .SelectMany(messages => messages)
            .Where(message => message.Role == ChatRole.System)
            .Select(message => message.Text ?? "")
            .ToList();
        Assert.All(systemTexts, text =>
        {
            // 提案记忆与经验注入不得出现在复盘员请求里（块 6 只给交易员）。
            Assert.DoesNotContain("decision_memory", text);
            Assert.DoesNotContain("历史决策记忆", text);
            Assert.DoesNotContain("复盘经验（最近", text);
        });

        // 复盘员装配位不提供任何上下文提供器。
        Assert.Empty(ReviewerAgent.New(env.Client, env.Config, []).AIContextProviders!);
    }

    // —— 工具集与审计：复用 ToolCatalog.Reviewer，审计行 CycleId 为 null ——

    [Fact]
    public async Task Reviewer_tool_audit_rows_have_null_cycle_id()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "report_with_tool");

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("completed", result.Status);
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer" && r.Status == "completed");
        var rows = await env.Db.ToolInvocations.Where(t => t.StageRunId == stage.Id).ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Null(row.CycleId));
        Assert.Equal(rows.Count, stage.ToolCallCount);
        Assert.All(rows, row => Assert.Contains(row.ToolName, ToolCatalog.Reviewer));
        // 复盘取数必须带窗口起点（否则默认只查最近 7 天，与「上次成功复盘之后」不符）。
        Assert.All(rows, row => Assert.Contains("sinceUtc", row.ArgumentsJson));
    }

    [Fact]
    public void Reviewer_tool_set_is_the_single_catalog_list()
    {
        // 复盘员工具只来自 ToolCatalog.Reviewer（不另起第二份清单，不混入交易员记忆工具）。
        Assert.Equal(["get_closed_trades", "get_cycles", "get_lessons"], ToolCatalog.Reviewer);
        Assert.DoesNotContain("place_order", ToolCatalog.Reviewer);
        Assert.DoesNotContain("get_decision_memory", ToolCatalog.Reviewer);
    }

    // —— R-13-5：per-trader 互斥，且绝不占用交易周期闸 ——

    [Fact]
    public async Task Concurrent_trigger_for_the_same_trader_is_rejected_without_a_second_run()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "block");

        var first = Task.Run(() => env.Runner.RunAsync(env.Trader, default));
        await env.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await env.Runner.RunAsync(env.Trader, default);

        // 复盘进行中：第二个触发直接拒绝，不排队、不并行写同一交易员的经验。
        Assert.Equal("in_progress", second.Status);
        Assert.Equal("review_in_progress", second.ErrorCode);
        Assert.Empty(env.Db.TraderLessons);

        env.Client.ReleaseBlockingCall();
        Assert.Equal("completed", (await first).Status);
        Assert.Single(env.Db.TraderLessons);
        Assert.Single(await env.Db.AgentStageRuns.Where(r => r.Role == "reviewer").ToListAsync());
    }

    [Fact]
    public async Task Review_gate_is_per_trader_and_independent_of_the_trading_cycle()
    {
        // 复盘闸是「按交易员」的独立闸：交易员 7 复盘卡住不影响交易员 8 复盘；
        // 且 ReviewRunner 不依赖 TradingCycleRunner（不共用、也不占用交易周期闸）。
        Assert.DoesNotContain(typeof(TradingCycleRunner),
            typeof(ReviewRunner).GetConstructors()[0].GetParameters().Select(p => p.ParameterType));

        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "block");
        var other = env.AddSecondRunningTrader();

        var blocked = Task.Run(() => env.Runner.RunAsync(env.Trader, default));
        await env.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var concurrent = Task.Run(() => env.Runner.RunAsync(other, default));
        await WaitUntilAsync(() => env.Client.Calls >= 2, "第二个交易员的复盘没有被第一个的闸挡住");

        // 独立性已经由上一行证明（7 卡住时 8 已进到模型）。收尾改为逐个放行：
        // 夹具里所有 AppDbContext 共用同一条 :memory: 连接，SQLite 不支持两个并发写者，
        // 同时放行会让两次落库互相踩连接（生产 DI 每个 scope 一条独立连接，不存在这条约束）。
        env.Client.ReleaseBlockingCall();
        Assert.Equal("completed", (await blocked).Status);
        env.Client.ReleaseBlockingCall();
        Assert.Equal("completed", (await concurrent).Status);
    }

    // —— R-13-2 / R-13-4：每日调度（分钟 tick → 到点? → 派发）——

    [Fact]
    public async Task Scheduler_does_not_fire_when_review_is_disabled()
    {
        // R-13-4：Enabled=false 时调度不触发；RunAsync 本身仍可被手动端点使用（Task 14）。
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.Config.Review.Enabled = false;
        env.WriteConfig();

        Assert.Equal(0, await env.CreateScheduler().RunTickAsync(LocalAt("10:00"), default));
        Assert.Empty(env.Db.TraderLessons);

        // 手动路径不受调度开关影响。
        Assert.Equal("completed", (await env.Runner.RunAsync(env.Trader, default)).Status);
    }

    [Fact]
    public async Task Scheduler_fires_once_per_local_day()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.Config.Review.LocalTime = "00:10";
        env.WriteConfig();
        var scheduler = env.CreateScheduler();

        // 未到点不触发；到点触发一次；同日再次 tick 不复发。
        Assert.Equal(0, await scheduler.RunTickAsync(LocalAt("00:09"), default));
        Assert.Equal(1, await scheduler.RunTickAsync(LocalAt("00:10"), default));
        Assert.Equal(0, await scheduler.RunTickAsync(LocalAt("09:00"), default));
        Assert.Single(env.Db.TraderLessons);
    }

    [Fact]
    public async Task Daily_guard_is_restart_safe()
    {
        // 重启安全：进程内状态全空，但库里今天已有一次失败复盘 → 同日不再复发。
        // LC-STD-05 §1.5：时钟与种子行都取自同一个合成时刻（不用当前时间做断言输入）；
        // 双侧断言：同一时刻无阶段行的新夹具必须派发 1，证明这里的「0」不是「谁都还没到点」的假绿。
        var now = LocalAt("10:00");

        await using var unseeded = await ReviewEnv.CreateAsync(closedTrades: 1);
        Assert.Equal(1, await unseeded.CreateScheduler().RunTickAsync(now, default));
        Assert.Single(unseeded.Db.TraderLessons);

        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.SeedReviewStage("failed", now - TimeSpan.FromHours(1));

        Assert.Equal(0, await env.CreateScheduler().RunTickAsync(now, default));
        Assert.Empty(env.Db.TraderLessons);
    }

    [Fact]
    public async Task Scheduler_skips_a_day_that_already_produced_a_lesson()
    {
        // 今天已成功复盘（经验落库）→ 重启后的 tick 也不得复发。
        // 双侧断言同上：同一合成时刻，无当日行的新夹具必须派发 1。
        var now = LocalAt("10:00");

        await using var unseeded = await ReviewEnv.CreateAsync(closedTrades: 1);
        Assert.Equal(1, await unseeded.CreateScheduler().RunTickAsync(now, default));

        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.SeedReviewStage("completed", now - TimeSpan.FromHours(2));
        env.SeedLesson(now - TimeSpan.FromHours(2), "今天的经验");

        Assert.Equal(0, await env.CreateScheduler().RunTickAsync(now, default));
    }

    [Fact]
    public async Task Scheduler_backfills_once_when_the_app_was_down_across_the_trigger()
    {
        // 00:10 停机、当天 09:00 起来且当天没有任何复盘落地 → 补跑一次（窗口从上次成功复盘起算，不丢数据）。
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.Config.Review.LocalTime = "00:10";
        env.WriteConfig();
        var scheduler = env.CreateScheduler();

        Assert.Equal(1, await scheduler.RunTickAsync(LocalAt("09:00"), default));
        Assert.Equal(0, await scheduler.RunTickAsync(LocalAt("09:01"), default));
        Assert.Single(env.Db.TraderLessons);
    }

    [Fact]
    public async Task Scheduler_skips_traders_that_are_not_running()
    {
        // 双侧断言：同一合成时刻先「未运行 → 0」，再翻回运行 → 1，
        // 证明「0」是 IsRunning 挡下的，而不是整个 tick 谁都还没到点。
        var now = LocalAt("10:00");
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.Trader.IsRunning = false;
        env.WriteTrader();

        Assert.Equal(0, await env.CreateScheduler().RunTickAsync(now, default));
        Assert.Empty(env.Db.TraderLessons);

        env.Trader.IsRunning = true;
        env.WriteTrader();

        Assert.Equal(1, await env.CreateScheduler().RunTickAsync(now, default));
        Assert.Single(env.Db.TraderLessons);
    }

    [Fact]
    public async Task Scheduler_records_failure_and_still_reviews_the_next_trader()
    {
        // §9 失败=记录失败、不阻塞交易、下个触发点重试：一个交易员失败不影响其他交易员。
        // 脚本用 throw_first（只有第一个交易员的上游抛错），否则两个交易员都失败、
        // 与「另一个交易员仍产出经验」这组断言自相矛盾。时钟取合成时刻（LC-STD-05 §1.5）。
        var now = LocalAt("10:00");
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "throw_first");
        env.AddSecondRunningTrader();

        var dispatched = await env.CreateScheduler().RunTickAsync(now, default);

        Assert.Equal(2, dispatched);
        Assert.Equal(2, await env.Db.AgentStageRuns.Where(r => r.Role == "reviewer").CountAsync());
        Assert.Equal(1, await env.Db.AgentStageRuns
            .Where(r => r.Role == "reviewer" && r.Status == "failed").CountAsync());
        Assert.Equal(1, await env.Db.TraderLessons.CountAsync());
    }

    [Fact]
    public async Task Scheduler_tolerates_a_misconfigured_local_time()
    {
        // 脏 localTime 按「未到点」容忍：不派发、不抛给整个 tick。
        // 双侧断言：同一合成时刻把配置改回合法的 "00:10" 必须派发 1，证明「0」来自脏配置本身。
        var now = LocalAt("10:00");
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);
        env.Config.Review.LocalTime = "25:99";
        env.WriteConfig();

        Assert.Equal(0, await env.CreateScheduler().RunTickAsync(now, default));

        env.Config.Review.LocalTime = "00:10";
        env.WriteConfig();

        Assert.Equal(1, await env.CreateScheduler().RunTickAsync(now, default));
        Assert.Single(env.Db.TraderLessons);
    }

    // —— 标准条款补测：窗口交给模型（§9 窗口行）/ 复盘只读边界（§1.35、§9 限制行）/
    //    复盘耗时可观测（§10）/ 经验注入合计 ≤1,200 字（§9 注入行）——

    [Fact]
    public async Task User_prompt_hands_the_model_the_window_and_the_read_only_boundary()
    {
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1);

        await env.Runner.RunAsync(env.Trader, default);

        var lesson = env.Db.TraderLessons.Single();
        const string marker = "复盘窗口起点 ";
        var line = env.Client.Prompt.Split('\n').Single(l => l.StartsWith(marker, StringComparison.Ordinal));
        var since = DateTimeOffset.Parse(line[marker.Length..].Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        // §9 窗口行：窗口是宿主算好交给模型的，模型只能按这个起点取数（工具默认只查最近 7 天）。
        Assert.True((since.UtcDateTime - lesson.PeriodStart).Duration() < TimeSpan.FromSeconds(1),
            $"提示词窗口起点 {since.UtcDateTime:O} 应与落库窗口起点 {lesson.PeriodStart:O} 一致");
        Assert.Contains(lesson.PeriodEnd.ToString("O", CultureInfo.InvariantCulture), env.Client.Prompt);

        // §1.35 + §9「限制」行的提示词侧：复盘员只读、不改配置、不预测价格。
        Assert.Contains("只读复盘角色", env.Client.Prompt);
        Assert.Contains("不得修改策略、风控配置、交易员状态或历史交易记录", env.Client.Prompt);
        Assert.Contains("不得给出未来价格预测", env.Client.Prompt);
    }

    [Fact]
    public async Task Completed_review_is_observable_through_the_stage_row()
    {
        // LC-STD-01 §10：复盘耗时可观测——阶段行必须带耗时、消息记录与结构化输出。
        await using var env = await ReviewEnv.CreateAsync(closedTrades: 1, behavior: "report_with_tool");

        var result = await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal("completed", result.Status);
        Assert.True(result.ElapsedMs > 0);
        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "reviewer");
        Assert.Equal(result.ElapsedMs, stage.ElapsedMs);
        Assert.NotNull(stage.OutputJson);
        using var transcript = JsonDocument.Parse(stage.TranscriptJson);
        Assert.Equal(JsonValueKind.Array, transcript.RootElement.ValueKind);
        // 密钥与上游地址既不进 error，也不进 transcript / outputJson。
        Assert.DoesNotContain("sk-test", stage.TranscriptJson);
        Assert.DoesNotContain("invalid.test", stage.OutputJson);
    }

    [Fact]
    public async Task Maximum_size_lesson_stays_inside_the_injection_budget()
    {
        // §9 注入行（最近 3 份、合计 ≤1,200 字）必须对「复盘写出的最大经验」依然成立：
        // summary 取 LC-STD-03 §2.1 的列上限 2,000 字，注入块仍要裁到 1,200 字以内。
        await using var env = await ReviewEnv.CreateAsync();
        env.SeedLesson(DateTime.UtcNow.AddHours(-1), new string('总', 2_000));

        var memory = new TradingMemoryContextProvider(env.Reader, env.Config);
        var blocks = await memory.BuildContextAsync(default);

        Assert.NotNull(blocks.LessonsBlock);
        Assert.True(blocks.LessonsBlock!.Length <= 1_200,
            $"经验注入块 {blocks.LessonsBlock.Length} 字超 §9 上限");
    }

    // —— 夹具 ——

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail(message);
            await Task.Delay(20);
        }
    }

    private static IReadOnlyList<string> ReadArray(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Select(item => item.GetString()!).ToList();

    /// <summary>把本地 "HH:mm" 折成 UTC 时刻；与生产同一口径（LocalTime 按 TimeZoneInfo.Local 判定）。</summary>
    private static DateTime LocalAt(string hhmm)
    {
        var parts = hhmm.Split(':');
        var local = DateTime.Today
            .AddHours(int.Parse(parts[0], CultureInfo.InvariantCulture))
            .AddMinutes(int.Parse(parts[1], CultureInfo.InvariantCulture));
        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
    }

    /// <summary>
    /// 复盘夹具：内存 SQLite + 脚本化模型 + 绑定账户的假读取器 + 真 AccountTools/ToolAuditor 装配位。
    /// </summary>
    private sealed class ReviewEnv : IAsyncDisposable
    {
        public required AppDbContext Db { get; init; }
        public required SseEventHub Hub { get; init; }
        public required Trader Trader { get; init; }
        public required StrategyConfig Config { get; init; }
        public required FakeReviewReader Reader { get; init; }
        public required ScriptedReviewClient Client { get; init; }
        public required ReviewRunner Runner { get; init; }
        public required Provider Provider { get; init; }
        public required ExchangeAccount Account { get; init; }
        public required Strategy Strategy { get; init; }
        public required IServiceProvider Services { get; init; }

        private SqliteConnection Connection { get; init; } = null!;

        public static async Task<ReviewEnv> CreateAsync(int closedTrades = 0, int cycles = 0,
            string behavior = "report", int timeoutSeconds = ReviewRunner.DefaultTimeoutSeconds,
            bool failFirstSave = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var hub = new SseEventHub();
            var config = new StrategyConfig();
            config.CoinSource.StaticCoins = ["BTC", "ETH"];
            var provider = new Provider
            {
                Id = 1, Name = "脚本提供商", Type = ProviderType.OpenAICompatible,
                BaseUrl = "https://invalid.test/v1", Model = "scripted", ApiKey = "sk-test",
                ContextTokens = 128_000
            };
            var account = new ExchangeAccount
            {
                Id = 1, ExchangeType = ExchangeType.GateIo, AccountName = "脚本账户",
                ApiKey = "k", ApiSecret = "s"
            };
            var strategy = new Strategy
            {
                Id = 1, Name = "脚本策略", ConfigJson = JsonSerializer.Serialize(config, VerificationJson.Options)
            };
            var trader = new Trader
            {
                Id = 7, Name = "脚本交易员", ProviderId = 1, ExchangeAccountId = 1, StrategyId = 1,
                ExecutionMode = "dry", ScanIntervalMinutes = 3, IsRunning = true
            };

            db.Providers.Add(provider);
            db.ExchangeAccounts.Add(account);
            db.Strategies.Add(strategy);
            db.Traders.Add(trader);
            await db.SaveChangesAsync();

            var reader = new FakeReviewReader(db, trader);
            for (var i = 0; i < closedTrades; i++) reader.AddClosedTrade();
            for (var i = 0; i < cycles; i++) reader.AddCycle(trader.Id);

            var client = new ScriptedReviewClient(behavior);
            var services = new ServiceCollection();
            if (failFirstSave) services.AddScoped<AppDbContext>(_ => new FailFirstSaveDbContext(options));
            else services.AddScoped(_ => new AppDbContext(options));
            services.AddSingleton(hub);
            services.AddSingleton<ChatClientFactory>(client);
            var serviceProvider = services.BuildServiceProvider();

            return new ReviewEnv
            {
                Db = db,
                Hub = hub,
                Trader = trader,
                Provider = provider,
                Account = account,
                Strategy = strategy,
                Config = config,
                Reader = reader,
                Client = client,
                Services = serviceProvider,
                Runner = new SeamRunner(serviceProvider.GetRequiredService<IServiceScopeFactory>(), hub,
                    reader, timeoutSeconds),
                Connection = connection,
            };
        }

        /// <summary>种一份历史经验（绝对时刻入参，LC-STD-05 §1.5：行时刻由调用方给定，不从当前时钟派生）。</summary>
        public void SeedLesson(DateTime at, string summary)
        {
            var stageId = Ids.NewUlid();
            Db.AgentStageRuns.Add(new AgentStageRun
            {
                Id = stageId, CycleId = null, TraderId = Trader.Id, Role = "reviewer", Status = "completed",
                ProviderName = Provider.Name, Model = Provider.Model,
                Instructions = PromptLibrary.Build(AgentRole.Reviewer, Config),
                TranscriptJson = "[]", ElapsedMs = 10, ToolCallCount = 0, CreatedAt = at
            });
            Db.TraderLessons.Add(new TraderLesson
            {
                TraderId = Trader.Id, StageRunId = stageId, Summary = summary,
                StrengthsJson = "[]", MistakesJson = "[]", RulesJson = "[]", EvidenceJson = "[]",
                PeriodStart = at.AddDays(-1), PeriodEnd = at, CreatedAt = at,
            });
            Db.SaveChanges();
        }

        /// <summary>只留阶段行（不写经验）：用于「今天已失败/已跳过」的重启安全守卫；绝对时刻入参同上。</summary>
        public void SeedReviewStage(string status, DateTime createdAt)
        {
            Db.AgentStageRuns.Add(new AgentStageRun
            {
                Id = Ids.NewUlid(), CycleId = null, TraderId = Trader.Id, Role = "reviewer", Status = status,
                ProviderName = Provider.Name, Model = Provider.Model,
                Instructions = PromptLibrary.Build(AgentRole.Reviewer, Config),
                TranscriptJson = "[]", ElapsedMs = 5, ToolCallCount = 0,
                CreatedAt = createdAt,
                Error = status == "failed" ? "stage_failed：复盘阶段执行失败，本轮未产出经验" : null
            });
            Db.SaveChanges();
        }

        /// <summary>再加一个运行中的交易员（同账户、同策略、同提供商）。</summary>
        public Trader AddSecondRunningTrader()
        {
            var second = new Trader
            {
                Id = 8, Name = ScriptedReviewClient.SecondTraderName, ProviderId = Trader.ProviderId,
                ExchangeAccountId = Trader.ExchangeAccountId, StrategyId = Trader.StrategyId,
                ExecutionMode = "dry", ScanIntervalMinutes = 3, IsRunning = true
            };
            Db.Traders.Add(second);
            Db.SaveChanges();
            Reader.BindTo(second);
            return second;
        }

        public void WriteConfig()
        {
            Strategy.ConfigJson = JsonSerializer.Serialize(Config, VerificationJson.Options);
            Db.SaveChanges();
        }

        public void WriteTrader() => Db.SaveChanges();

        public ReviewScheduler CreateScheduler() => new(Runner, Services.GetRequiredService<IServiceScopeFactory>());

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }

        private sealed class SeamRunner(IServiceScopeFactory scopeFactory, SseEventHub hub,
            IAccountReader reader, int timeoutSeconds)
            : ReviewRunner(scopeFactory, hub)
        {
            protected override int TimeoutSeconds => timeoutSeconds;

            protected override IAccountReader CreateReader(IServiceProvider services, Trader trader,
                ExchangeAccount account, StrategyConfig config, string memoryFingerprint) => reader;
        }

        /// <summary>
        /// 模拟「落库基础设施第一次写就抛错」（数据库不可用等，6cfccc2 的成因）：
        /// 只让第一次 <see cref="SaveChangesAsync"/> 失败，之后一律走基类——失败补写必须能在同一
        /// ChangeTracker 里把未提交的阶段行改写为 failed 并落库，而不是再 <c>Add</c> 一份撞身份。
        /// </summary>
        private sealed class FailFirstSaveDbContext(DbContextOptions<AppDbContext> options)
            : AppDbContext(options)
        {
            private int _saveCalls;

            public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
                ++_saveCalls == 1
                    ? Task.FromException<int>(new DbUpdateException("模拟落库失败",
                        new InvalidOperationException("database is locked")))
                    : base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 绑定「账户 + 交易员 + 策略」的假读取器：只提供复盘员真正需要的三类数据
    /// （已平仓交易 / 周期 / 经验），经验直接从库里读，故能证明「本轮写的经验下一轮被注入」。
    /// 与生产 <see cref="AccountReader"/> 同样不接受调用方传入的归属参数。
    /// </summary>
    private sealed class FakeReviewReader(AppDbContext db, Trader trader) : IAccountReader
    {
        private readonly List<PositionHistory> _trades = [];
        private readonly List<TradingCycle> _cycles = [];
        private Trader _trader = trader;

        public int ClosedTradeCalls { get; private set; }
        public int CycleCalls { get; private set; }

        public void BindTo(Trader trader) => _trader = trader;

        public void AddClosedTrade(DateTime? closeTime = null) => _trades.Add(new PositionHistory
        {
            ExchangeAccountId = _trader.ExchangeAccountId, DedupeKey = Ids.NewUlid(), Source = "test",
            Symbol = "BTC/USDT", Side = "long", Quantity = 0.1m, EntryPrice = 100m, ExitPrice = 110m,
            RealizedPnl = 1m, Fee = 0.1m, CloseTime = closeTime ?? DateTime.UtcNow.AddMinutes(-30),
            SyncedAt = DateTime.UtcNow
        });

        public void AddCycle(int traderId) => _cycles.Add(new TradingCycle
        {
            Id = Ids.NewUlid(), TraderId = traderId, StrategyId = 1, ProviderId = 1, ExchangeAccountId = 1,
            Mode = "dry", Trigger = "schedule", Status = "completed", StartedAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = DateTime.UtcNow.AddHours(-1), StrategyConfigJson = "{}", MemoryFingerprint = "f"
        });

        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) =>
            Task.FromResult(new AccountSnapshot(true, 10_000m, 9_000m, null));

        public Task<PositionReadResult> GetPositionsAsync(CancellationToken ct) =>
            Task.FromResult(new PositionReadResult(true, [], null));

        public Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderOrder>>([]);

        public Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct)
        {
            ClosedTradeCalls++;
            return Task.FromResult<IReadOnlyList<PositionHistory>>(_trades
                .Where(trade => trade.ExchangeAccountId == _trader.ExchangeAccountId
                    && trade.CloseTime >= sinceUtc)
                .OrderByDescending(trade => trade.CloseTime)
                .Take(limit)
                .ToList());
        }

        public Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TradeProposal>>([]);

        public Task<RiskControlConfig> GetRiskPolicyAsync(CancellationToken ct) =>
            Task.FromResult(new RiskControlConfig());

        public Task<TradingDiscipline> GetDisciplineAsync(CancellationToken ct) =>
            Task.FromResult(TradingRiskPolicy.EvaluateDiscipline([], DateTime.UtcNow, TimeZoneInfo.Local));

        public Task<IReadOnlyList<TradingCycle>> GetCyclesAsync(DateTime sinceUtc, int limit, CancellationToken ct)
        {
            CycleCalls++;
            return Task.FromResult<IReadOnlyList<TradingCycle>>(_cycles
                .Where(cycle => cycle.TraderId == _trader.Id && cycle.StartedAt >= sinceUtc)
                .OrderByDescending(cycle => cycle.StartedAt)
                .Take(limit)
                .ToList());
        }

        public async Task<IReadOnlyList<TraderLesson>> GetLessonsAsync(int limit, CancellationToken ct) =>
            await db.TraderLessons.AsNoTracking()
                .Where(lesson => lesson.TraderId == _trader.Id)
                .OrderByDescending(lesson => lesson.CreatedAt)
                .Take(limit)
                .ToListAsync(ct);
    }

    /// <summary>
    /// 脚本化复盘模型：固定输出 <see cref="ReviewReport"/> JSON / 抛错 / 卡住 / 连续打满工具预算。
    /// </summary>
    private sealed class ScriptedReviewClient : ChatClientFactory, IChatClient
    {
        private const string Summary = "复盘结论：守住止损就是纪律";

        /// <summary>第二个交易员的名称：脚本模型据此只让第一个交易员的上游抛错。</summary>
        public const string SecondTraderName = "第二交易员";

        private static readonly string ReportJson = JsonSerializer.Serialize(new ReviewReport(
            Summary,
            ["先等回踩再开仓", "手续费计入盈亏比"],
            ["追高一次未设止损"],
            ["不开没有止损的仓位", "平仓后 24 小时内不复购同币"],
            ["01TESTORDER0000000000000A", "01TESTCYCLE0000000000000B"]), AgentJson.Options);

        private readonly string _behavior;
        private readonly SemaphoreSlim _release = new(0, 1);
        private int _toolRound;

        public ScriptedReviewClient(string behavior) => _behavior = behavior;

        public int Calls { get; private set; }

        /// <summary>上游进入标记：<see cref="Entered"/> 与 <see cref="ReleaseBlockingCall"/> 用的同一个 TCS。</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Prompt { get; private set; } = "";
        public List<IReadOnlyList<ChatMessage>> CapturedMessages { get; } = [];

        public override IChatClient Create(Provider provider) => this;

        public void ReleaseBlockingCall() => _release.Release();

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var list = messages.ToList();
            CapturedMessages.Add(list);
            Prompt = list.LastOrDefault()?.Text ?? "";

            switch (_behavior)
            {
                case "throw":
                    throw new HttpRequestException("上游炸了 https://sk-test@invalid.test/v1 502");
                case "throw_first":
                    // 只有第一个交易员的复盘会失败：证明「一个交易员失败不阻塞其他交易员」（§9 失败行）。
                    if (!Prompt.Contains(SecondTraderName, StringComparison.Ordinal))
                    {
                        throw new HttpRequestException("上游炸了 https://sk-test@invalid.test/v1 502");
                    }

                    return Chat(ReportJson);
                case "empty":
                    return Chat("""
                        {"summary":"","strengths":[],"mistakes":[],"rules":[],"evidence":[]}
                        """);
                case "oversize":
                    return Chat(JsonSerializer.Serialize(new ReviewReport(
                        new string('总', 2600), [], [],
                        Enumerable.Range(0, 14).Select(_ => new string('规', 260)).ToList(),
                        ["01TESTORDER0000000000000A"]), AgentJson.Options));
                case "hang":
                    Entered.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    return Chat(ReportJson);
                case "block":
                    Entered.TrySetResult();
                    await _release.WaitAsync(cancellationToken);
                    return Chat(ReportJson);
                case "tool_loop":
                    // 连续 12 轮工具调用：第 11 轮起被 10 工具上限拒绝（R-13-6）。
                    if (++_toolRound <= 12) return ToolCall("get_lessons", new Dictionary<string, object?>
                    {
                        ["limit"] = 1
                    });
                    return Chat(ReportJson);
                case "report_with_tool":
                    // 首轮按窗口取数（真实工具循环），次轮产出报告。
                    if (++_toolRound == 1) return ToolCall("get_cycles", new Dictionary<string, object?>
                    {
                        ["sinceUtc"] = PromptWindowStart(), ["limit"] = 5
                    });
                    return Chat(ReportJson);
                default:
                    return Chat(ReportJson);
            }
        }

        private static ChatResponse ToolCall(string tool, Dictionary<string, object?> args) =>
            new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{Guid.NewGuid():N}", tool, args)]));

        private static ChatResponse Chat(string json) =>
            new(new ChatMessage(ChatRole.Assistant, json));

        /// <summary>从用户提示词里取本轮复盘窗口起点（验证提示词确实把窗口交给模型）。</summary>
        private string? PromptWindowStart()
        {
            var marker = "窗口起点 ";
            var index = Prompt.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return null;
            var rest = Prompt[(index + marker.Length)..];
            var end = rest.IndexOf('\n');
            return (end < 0 ? rest : rest[..end]).Trim();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
