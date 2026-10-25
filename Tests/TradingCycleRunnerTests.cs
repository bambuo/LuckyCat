using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Tools;
using LuckyCat.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace LuckyCat.Tests;

/// <summary>
/// Task 11：P4 周期宿主（预算、SSE、终态保证与重启恢复）。
/// 全部使用脚本化假 IChatClient + 假 IAccountReader / IMarketDataReader / IExecutionGateway + 内存 SQLite，
/// 不访问真实模型与交易所（LC-STD-05 §2「工作流层」、§3 注入缝）。
/// </summary>
public class TradingCycleRunnerTests(ITestOutputHelper output)
{
    // —— 必测：阶段失败与重启恢复 ——

    [Fact]
    public async Task Risk_failure_marks_cycle_failed_without_verdict()
    {
        await using var env = await RunnerEnv.CreateAsync(riskBehavior: "throw");

        await env.Runner.RunAsync(env.Trader, default);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("failed", cycle.Status);
        Assert.Equal("risk_stage_failed", cycle.ErrorCode);
        Assert.Empty(env.Db.RiskVerdicts);
        Assert.Empty(env.Db.TraderOrders);
        // 失败阶段本身必须留痕（LC-STD-01 §7「写失败阶段」）。
        var failedStage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "risk");
        Assert.Equal("failed", failedStage.Status);
        // 风控之后的执行阶段没有被路由（fail-closed 不下单）。
        Assert.Empty(await env.Db.AgentStageRuns.Where(r => r.Role == "executor").ToListAsync());
        // R-11-1：原始异常文本既不落周期，也不落阶段行。
        Assert.DoesNotContain("stage exploded", cycle.Error);
        Assert.DoesNotContain("stage exploded", failedStage.Error);
        output.WriteLine($"risk failure: code={cycle.ErrorCode} error={cycle.Error}");
    }

    [Fact]
    public async Task Running_cycle_is_marked_aborted_on_startup()
    {
        await using var env = await RunnerEnv.CreateAsync();
        env.Db.TradingCycles.Add(new TradingCycle
        {
            Id = "old", TraderId = 1, StrategyId = 1, ProviderId = 1, ExchangeAccountId = 1,
            Mode = "dry", Trigger = "schedule", Status = "running",
            StartedAt = DateTime.UtcNow, StrategyConfigJson = "{}", MemoryFingerprint = "f"
        });
        await env.Db.SaveChangesAsync();

        await env.Runner.RecoverAsync();

        // 夹具的写入上下文与宿主的恢复上下文是两个实例：读回时不取跟踪项，避免身份解析返回旧值。
        var cycle = await env.Db.TradingCycles.AsNoTracking().SingleAsync();
        Assert.Equal("aborted", cycle.Status);
        Assert.Equal("process_restart", cycle.ErrorCode);
        Assert.NotNull(cycle.CompletedAt);
    }

    [Fact]
    public async Task Startup_recovery_resumes_is_running_traders_and_aborts_stale_cycles()
    {
        // R-14-5 切流（LC-STD-01 §8.7、LC-STD-05 FLOW-13）：启动宿主先收敛遗留 running
        // （aborted + process_restart、交易员侧同步收口），再为 IsRunning 交易员经
        // **新链路** TradingCycleRunner.Start 恢复调度——旧 TraderRunnerService 的 hosted 注册
        // 已随切流摘除（注册形状由 ApiContractTests 的 Program 源码扫描钉死），本进程恰好一条调度链。
        await using var env = await RunnerEnv.CreateAsync();
        env.Trader.IsRunning = true;
        env.Trader.LastCycleStatus = "running";
        env.Db.TradingCycles.Add(new TradingCycle
        {
            Id = "stale", TraderId = 7, StrategyId = 1, ProviderId = 1, ExchangeAccountId = 1,
            Mode = "dry", Trigger = "schedule", Status = "running",
            StartedAt = DateTime.UtcNow, StrategyConfigJson = "{}", MemoryFingerprint = "f"
        });
        await env.Db.SaveChangesAsync();

        var hosted = new CycleStartupRecovery(env.Runner, env.ScopeFactory);
        try
        {
            await hosted.StartAsync(default);
            // 恢复发生在 ExecuteAsync 的后台续跑里：轮询到「遗留行已收敛 + 新链路已接管调度」。
            await WaitUntilAsync(async () =>
                "aborted" == (await env.Db.TradingCycles.AsNoTracking().SingleAsync(c => c.Id == "stale")).Status
                && env.Runner.IsRunning(env.Trader.Id)
                && await env.Db.TradingCycles.AsNoTracking().AnyAsync(c => c.Trigger == "schedule"));
            await hosted.StopAsync(default);

            var stale = await env.Db.TradingCycles.AsNoTracking().SingleAsync(c => c.Id == "stale");
            Assert.Equal("aborted", stale.Status);
            Assert.Equal("process_restart", stale.ErrorCode);
            Assert.NotNull(stale.CompletedAt);

            // 续跑只发生一次：除遗留行外恰好一条新的定时周期（同交易员周期闸 + Start 幂等）。
            Assert.True(env.Runner.IsRunning(env.Trader.Id));
            Assert.Equal(2, await env.Db.TradingCycles.CountAsync());
            var resumed = await env.Db.TradingCycles.AsNoTracking().SingleAsync(c => c.Id != "stale");
            Assert.Equal("schedule", resumed.Trigger);
            Assert.Equal(env.Trader.Id, resumed.TraderId);
        }
        finally
        {
            // 停掉恢复起来的循环，绝不留 running 给夹具释放后的内存库（终态守卫兜底）。
            env.Runner.Stop(env.Trader.Id, "process_restart");
            await WaitUntilAsync(async () =>
                !await env.Db.TradingCycles.AsNoTracking().AnyAsync(c => c.Status == "running"));
        }
    }

    // —— 预算 ——

    [Fact]
    public void Tool_budget_enforces_per_role_and_per_cycle_limits()
    {
        var budget = new CycleBudget(new AgentBudgetConfig());

        for (var i = 0; i < 12; i++) Assert.True(budget.TryConsumeTool("analyst"));
        Assert.False(budget.TryConsumeTool("analyst"));
        // 单角色超限不影响其他角色（阶段预算彼此独立，超出不抛异常）。
        Assert.True(budget.TryConsumeTool("risk"));
        Assert.Equal(13, budget.TotalToolCalls);

        // 12/8/4/6 的角色上限恰好等于周期上限 30：填满后任何角色都不再放行。
        for (var i = 0; i < 8; i++) Assert.True(budget.TryConsumeTool("trader"));
        for (var i = 1; i < 4; i++) Assert.True(budget.TryConsumeTool("risk"));
        for (var i = 0; i < 6; i++) Assert.True(budget.TryConsumeTool("executor"));
        Assert.Equal(30, budget.TotalToolCalls);
        Assert.False(budget.TryConsumeTool("executor"));
        Assert.False(budget.TryConsumeTool("analyst"));
        Assert.Equal(8, budget.ToolCalls("trader"));
    }

    [Fact]
    public async Task Tool_budget_exhaustion_is_audited_and_never_throws()
    {
        await using var env = await RunnerEnv.CreateAsync();
        var budget = new CycleBudget(env.Config.AgentBudget);
        var auditor = new ToolAuditor(env.Db, env.Hub, budget, env.CycleId, env.StageRunId,
            SseChannels.Trader(7));
        var tools = new MarketTools(new MarketDataCache(env.MarketReader), env.Config,
            new HashSet<string>(StringComparer.Ordinal) { "BTC/USDT" }, auditor, "analyst");

        for (var i = 0; i < 12; i++)
        {
            var ok = await tools.GetKlinesAsync("BTC/USDT", "5m", 30, false, default);
            Assert.True(ok.Ok);
        }

        var exhausted = await tools.GetKlinesAsync("BTC/USDT", "5m", 30, false, default);
        Assert.False(exhausted.Ok);
        Assert.Equal(ToolErrors.ToolBudgetExceeded, exhausted.Error!.Code);
        Assert.False(budget.TryConsumeTool("analyst"));
        // 超限后不再执行工具本体：第 13 次只有被拒绝的审计行。
        var rows = await env.Db.ToolInvocations.Where(i => i.StageRunId == env.StageRunId).ToListAsync();
        Assert.Equal(13, rows.Count);
        Assert.Equal(1, rows.Count(r => !r.Success));
        Assert.Equal("tool_budget_exceeded", rows.Single(r => !r.Success).ErrorCode);
    }

    [Fact]
    public void Input_token_budget_follows_the_standard_formula()
    {
        var config = new AgentBudgetConfig();

        // ContextTokens=0 → 取 150,000。
        Assert.Equal(150_000, CycleBudget.ComputeInputTokenBudget(config, ScriptedProvider(0)));
        // min(150000, ContextTokens − effectiveMaxOutputTokens)。
        Assert.Equal(43_616, CycleBudget.ComputeInputTokenBudget(config, ScriptedProvider(60_000)));
        Assert.Equal(133_616, CycleBudget.ComputeInputTokenBudget(config, ScriptedProvider(150_000)));
        // 下限 1,000：小上下文模型不得出现零或负预算。
        Assert.Equal(1_000, CycleBudget.ComputeInputTokenBudget(config, ScriptedProvider(1_024)));
        // MaxOutputTokens<=0 时按 16,384 计。
        Assert.Equal(3_616, CycleBudget.ComputeInputTokenBudget(config,
            new Provider { Id = 1, Name = "p", Type = ProviderType.Ollama, BaseUrl = "http://127.0.0.1",
                Model = "m", ContextTokens = 20_000, MaxOutputTokens = 0 }));
        // 配置上限收紧时，预算随配置而非硬编码常量。
        var capped = new AgentBudgetConfig { InputTokenBudget = 20_000 };
        Assert.Equal(20_000, CycleBudget.ComputeInputTokenBudget(capped, ScriptedProvider(0)));
        Assert.Equal(3_616, CycleBudget.ComputeInputTokenBudget(capped, ScriptedProvider(20_000)));
    }

    [Fact]
    public async Task Risk_stage_timeout_is_fail_closed_without_verdict_or_order()
    {
        // FLOW-08：风控超时按阶段失败处理（不是周期总超时），裁决与订单都不产生。
        await using var env = await RunnerEnv.CreateAsync(riskBehavior: "hang", riskTimeoutSeconds: 1);

        await env.Runner.RunAsync(env.Trader, default);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("failed", cycle.Status);
        Assert.Equal("risk_stage_failed", cycle.ErrorCode);
        Assert.Empty(env.Db.RiskVerdicts);
        Assert.Empty(env.Db.TraderOrders);
        Assert.Equal("failed", (await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "risk")).Status);
        // 分析/交易两段正常落库，风控段按失败落库，执行段根本没有运行（fail-closed 不下单）。
        Assert.Equal(["analyst", "risk", "trader"],
            (await env.Db.AgentStageRuns.ToListAsync()).Select(r => r.Role).OrderBy(r => r, StringComparer.Ordinal).ToArray());
        Assert.Equal("completed", (await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "trader")).Status);
    }

    [Fact]
    public void Stage_and_cycle_timeout_defaults_match_the_standard()
    {
        var budget = new CycleBudget(new AgentBudgetConfig());

        Assert.Equal(90, budget.StageTimeoutSeconds("analyst"));
        Assert.Equal(90, budget.StageTimeoutSeconds("trader"));
        Assert.Equal(45, budget.StageTimeoutSeconds("risk"));
        Assert.Equal(45, budget.StageTimeoutSeconds("executor"));
        Assert.Equal(300, budget.CycleTimeoutSeconds);
    }

    // —— 终态：超时 / 用户取消 / 宿主异常，一律不得留下 running ——

    [Fact]
    public async Task Cycle_timeout_marks_failed_and_cancels_the_inflight_stage()
    {
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "hang", cycleTimeoutSeconds: 1);

        await env.Runner.RunAsync(env.Trader, default);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("failed", cycle.Status);
        Assert.Equal("cycle_timeout", cycle.ErrorCode);
        Assert.NotNull(cycle.CompletedAt);
        Assert.Empty(env.Db.TraderOrders);
        Assert.True(env.Clients.StageObservedCancellation,
            "周期超时必须经 CancellationToken 传播到阶段内部（不得用 Task.WhenAny 遗留任务）");
    }

    [Fact]
    public async Task User_cancellation_records_aborted_with_cancelled_by_user()
    {
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "hang");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await env.Runner.RunAsync(env.Trader, cts.Token);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("aborted", cycle.Status);
        Assert.Equal("cancelled_by_user", cycle.ErrorCode);
        Assert.NotNull(cycle.CompletedAt);
        Assert.Empty(env.Db.TraderOrders);
    }

    [Fact]
    public async Task Host_exception_still_writes_a_terminal_state()
    {
        // 模型工厂在构图阶段抛错：图一段都没跑，终态仍必须由宿主的 finally 守卫写出。
        await using var env = await RunnerEnv.CreateAsync(clientFactoryBehavior: "throw");

        await env.Runner.RunAsync(env.Trader, default);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("failed", cycle.Status);
        Assert.Equal("cycle_internal_error", cycle.ErrorCode);
        Assert.NotNull(cycle.CompletedAt);
        // 原始异常文本（含上游标识）不得进入落库数据（R-11-1）。
        Assert.DoesNotContain("boom-of-upstream", cycle.Error);
        Assert.Empty(env.Db.AgentStageRuns);
    }

    [Fact]
    public async Task Token_budget_exhaustion_stops_later_stages_and_records_observation()
    {
        // FLOW-09：累计输入 token 达到预算后停止后续阶段，本周期观望——不提案、不裁决、不下单，
        // 且已完成（不是 failed）；只有真实跑过的分析师留下阶段记录。
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "burst");

        await env.Runner.RunAsync(env.Trader, default);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("completed", cycle.Status);
        Assert.Equal("token_budget_exceeded", cycle.ErrorCode);
        Assert.Equal(200_000, cycle.InputTokens);
        Assert.Equal(["analyst"],
            (await env.Db.AgentStageRuns.ToListAsync()).Select(s => s.Role).ToArray());
        Assert.Empty(env.Db.TradeProposals);
        Assert.Empty(env.Db.RiskVerdicts);
        Assert.Empty(env.Db.TraderOrders);
    }

    [Fact]
    public async Task Analyst_failure_marks_cycle_failed_without_proposal()
    {
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "throw");

        await env.Runner.RunAsync(env.Trader, default);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("failed", cycle.Status);
        Assert.Equal("stage_failed", cycle.ErrorCode);
        Assert.Empty(env.Db.TradeProposals);
        Assert.Equal("failed", (await env.Db.AgentStageRuns.SingleAsync()).Status);
    }

    [Fact]
    public async Task Token_cap_crossed_midstage_stops_the_next_model_call()
    {
        // 修复轮 2-C（LC-STD-01 §6「预算在每次模型调用前累计检查，超限不再发起调用」）：
        // 工具循环第一轮就打满预算（prompt 2,000 ≥ 上限 2,000），第二轮必须被调用前闸拦下——
        // 断言的是 inner client 的实际调用次数（1 次），不是只看结果。周期按既有阶段失败路径
        // 归因到 token_budget_exceeded，原始拒绝文本不外泄。
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "overcap", inputTokenBudget: 2_000);

        await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal(1, env.Clients.CallsFor("analyst"));
        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("failed", cycle.Status);
        Assert.Equal("token_budget_exceeded", cycle.ErrorCode);
        Assert.DoesNotContain("token budget gate refused", cycle.Error);
        var stage = await env.Db.AgentStageRuns.SingleAsync();
        Assert.Equal("analyst", stage.Role);
        Assert.Equal("failed", stage.Status);
        Assert.Contains("token_budget_exceeded", stage.Error);
        Assert.Empty(env.Db.TradeProposals);
        Assert.Empty(env.Db.TraderOrders);
    }

    // —— FLOW-07 交易员契约失败重试 1 次（LC-STD-05:98）——

    [Fact]
    public async Task Trader_contract_failure_retries_once_and_continues_on_valid_proposal()
    {
        // 首轮越界 → 重试 1 次得到合法 open 提案：周期照常走完，Attempt=2 落库。
        await using var env = await RunnerEnv.CreateAsync(traderBehavior: "invalid-once");

        await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal(2, env.Clients.CallsFor("trader"));
        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("completed", cycle.Status);
        Assert.Null(cycle.ErrorCode);
        var traderStage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "trader");
        Assert.Equal(2, traderStage.Attempt);
        Assert.Equal("completed", traderStage.Status);
        // 重试轮提案正常路由：越界首轮没有生效，风控/执行照常。
        var proposal = await env.Db.TradeProposals.SingleAsync();
        Assert.Equal("open", proposal.Action);
        Assert.NotNull(await env.Db.RiskVerdicts.SingleAsync());
        Assert.NotNull(await env.Db.AgentStageRuns.SingleOrDefaultAsync(r => r.Role == "executor"));
    }

    [Fact]
    public async Task Trader_contract_failure_after_retry_is_fail_closed_with_single_terminal_write()
    {
        // 重试仍越界 → 维持既有 fail-closed：failed + stage_contract_violation + Hold 归一化收尾。
        // 终态恰好写一次（宿主的守卫不再补写）；无后续阶段、无订单；Attempt=2 落库。
        await using var env = await RunnerEnv.CreateAsync(traderBehavior: "invalid");
        var stream = env.Subscribe(env.Trader.Id);

        await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal(2, env.Clients.CallsFor("trader"));
        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("failed", cycle.Status);
        Assert.Equal("stage_contract_violation", cycle.ErrorCode);
        var traderStage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "trader");
        Assert.Equal(2, traderStage.Attempt);
        var proposal = await env.Db.TradeProposals.SingleAsync();
        Assert.Equal("hold", proposal.Action);
        Assert.Empty(await env.Db.AgentStageRuns.Where(r => r.Role == "risk" || r.Role == "executor").ToListAsync());
        Assert.Empty(env.Db.TraderOrders);
        // 终态 cycle 事件恰好一次（开始 + 终态 = 2）：守卫没有重复终态写入。
        Assert.Equal(2, stream.Drain().Count(e => e.Name == "cycle"));
    }

    // —— 成功路径：落库与 SSE 负载契约 ——

    [Fact]
    public async Task Open_cycle_persists_stages_proposal_and_verdict_with_executor_stage_run_ids()
    {
        await using var env = await RunnerEnv.CreateAsync();

        var cycleId = await env.Runner.RunAsync(env.Trader, default);
        Assert.NotNull(cycleId);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("completed", cycle.Status);
        Assert.Null(cycle.ErrorCode);
        Assert.NotNull(cycle.CompletedAt);
        Assert.Equal(4, await env.Db.AgentStageRuns.CountAsync(r => r.CycleId == cycle.Id));
        Assert.Equal(["analyst", "executor", "risk", "trader"],
            (await env.Db.AgentStageRuns.Where(r => r.CycleId == cycle.Id).Select(r => r.Role).ToListAsync())
            .OrderBy(r => r, StringComparer.Ordinal).ToArray());

        var proposal = await env.Db.TradeProposals.SingleAsync();
        Assert.Equal(cycle.Id, proposal.CycleId);
        Assert.Equal(env.Trader.Id, proposal.TraderId);
        Assert.Equal("open", proposal.Action);
        Assert.Equal("BTC/USDT", proposal.Symbol);
        Assert.Equal("long", proposal.Side);
        var verdict = await env.Db.RiskVerdicts.SingleAsync();
        Assert.Equal(proposal.Id, verdict.ProposalId);
        Assert.Equal("approved", verdict.Decision);
        Assert.NotNull(verdict.ValidUntilUtc);

        // 阶段工具审计行的 stageRunId 与 AgentStageRun.Id 同源（宿主复用执行器的 ID，不另造）；
        // 真实工具循环里分析师至少调用了一次读工具。
        Assert.True(env.MarketReader.CallCount > 0);
        var stageIds = (await env.Db.AgentStageRuns.ToListAsync()).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var toolRows = await env.Db.ToolInvocations.ToListAsync();
        Assert.NotEmpty(toolRows);
        Assert.All(toolRows, row => Assert.Contains(row.StageRunId, stageIds));
        Assert.All(toolRows, row => Assert.Equal(cycle.Id, row.CycleId));

        // 阶段行的模型快照与用量按周期来源写入。
        var analystStage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "analyst");
        Assert.Equal(env.Provider.Name, analystStage.ProviderName);
        Assert.Equal(env.Provider.Model, analystStage.Model);
        Assert.Equal(env.Trader.Id, analystStage.TraderId);
        Assert.Equal("completed", analystStage.Status);

        // 交易员的最近周期状态按约定枚举写回（state 事件的数据源）；同上不取夹具的跟踪项。
        var trader = await env.Db.Traders.AsNoTracking().SingleAsync(t => t.Id == env.Trader.Id);
        Assert.Equal("completed", trader.LastCycleStatus);
        Assert.NotNull(trader.LastCycleAt);
    }

    [Fact]
    public async Task Sse_events_follow_the_contracted_payload_shapes()
    {
        await using var env = await RunnerEnv.CreateAsync();
        var stream = env.Subscribe(env.Trader.Id);

        var cycleId = await env.Runner.RunAsync(env.Trader, default);
        var received = stream.Drain();

        Assert.Equal("cycle", received[0].Name);
        Assert.Contains("stage", received.Select(e => e.Name));
        Assert.Contains("proposal", received.Select(e => e.Name));
        Assert.Contains("verdict", received.Select(e => e.Name));
        // 终态 cycle 事件恰好一次：Finalizer 写过之后宿主的守卫不再补写（不重复计数）。
        Assert.Equal(2, received.Count(e => e.Name == "cycle"));

        var started = Json(received[0]);
        Assert.Equal("running", started.GetProperty("status").GetString());
        Assert.Equal(cycleId, started.GetProperty("cycleId").GetString());
        Assert.Equal(env.Trader.Id, started.GetProperty("traderId").GetInt32());
        Assert.True(started.TryGetProperty("completedAt", out var startCompleted) && startCompleted.ValueKind == JsonValueKind.Null);
        Assert.True(started.TryGetProperty("error", out var startError) && startError.ValueKind == JsonValueKind.Null);

        var completed = Json(received[^1]);
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal("dry", completed.GetProperty("mode").GetString());
        Assert.Equal("schedule", completed.GetProperty("trigger").GetString());
        Assert.Equal(JsonValueKind.String, completed.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, completed.GetProperty("completedAt").ValueKind);

        var stage = Json(received.First(e => e.Name == "stage"));
        foreach (var field in new[] { "cycleId", "stageId", "role", "status", "attempt", "elapsedMs",
                     "inputTokens", "outputTokens", "toolCalls", "summary", "error" })
        {
            Assert.True(stage.TryGetProperty(field, out _), $"stage 事件缺少约定字段 {field}");
        }
        Assert.Equal("completed", stage.GetProperty("status").GetString());
        Assert.Equal("analyst", stage.GetProperty("role").GetString());

        var proposal = Json(received.First(e => e.Name == "proposal"));
        Assert.Equal("open", proposal.GetProperty("action").GetString());
        // REST/SSE 的数值一律字符串（LC-STD-03 语义规则 4）。
        Assert.Equal(JsonValueKind.String, proposal.GetProperty("entryPrice").ValueKind);
        Assert.Equal("100", proposal.GetProperty("entryPrice").GetString());

        var verdict = Json(received.First(e => e.Name == "verdict"));
        Assert.Equal("approved", verdict.GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.String, verdict.GetProperty("maxLeverage").ValueKind);
        Assert.Equal(JsonValueKind.Array, verdict.GetProperty("reasons").ValueKind);
    }

    [Fact]
    public async Task Finalizer_terminal_state_is_not_overwritten_by_the_host_guard()
    {
        // 执行报告 errors 非空：终态由 Finalizer 按 completed + execution_errors 写入，守卫不得覆盖。
        await using var env = await RunnerEnv.CreateAsync(executorBehavior: "errors");
        var stream = env.Subscribe(env.Trader.Id);

        await env.Runner.RunAsync(env.Trader, default);

        var cycle = await env.Db.TradingCycles.SingleAsync();
        Assert.Equal("completed", cycle.Status);
        Assert.Equal("execution_errors", cycle.ErrorCode);
        Assert.Contains("拒单", cycle.Error);
        // 周期事件只有「开始 + 终态」两条：守卫没有补写第二份终态（不重复计数）。
        var received = stream.Drain();
        Assert.Equal(2, received.Count(e => e.Name == "cycle"));
        Assert.Equal("completed", Json(received[^1]).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Oversized_transcript_is_truncated_to_128_kib_and_flagged()
    {
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "huge");

        await env.Runner.RunAsync(env.Trader, default);

        var stage = await env.Db.AgentStageRuns.SingleAsync(r => r.Role == "analyst");
        Assert.True(stage.Truncated);
        var bytes = Encoding.UTF8.GetByteCount(stage.TranscriptJson);
        Assert.True(bytes <= 128 * 1024, $"消息落库必须不超过 128 KiB，实际 {bytes} 字节");
        // 截断后仍是合法 JSON（不得留下半个文档）。
        using var _ = JsonDocument.Parse(stage.TranscriptJson);
        output.WriteLine($"transcript bytes={bytes} truncated={stage.Truncated}");
    }

    // —— 并发 / 手动平仓 / 试运行 ——

    [Fact]
    public async Task Concurrent_runs_for_one_trader_produce_a_single_cycle()
    {
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "slow");

        var results = await Task.WhenAll(env.Runner.RunAsync(env.Trader, default),
            env.Runner.RunAsync(env.Trader, default));

        Assert.Equal(1, await env.Db.TradingCycles.CountAsync());
        Assert.Equal(1, results.Count(id => id is not null));
        Assert.Equal(1, results.Count(id => id is null));
    }

    [Fact]
    public async Task Manual_close_creates_manual_cycle_and_publishes_order_event()
    {
        await using var env = await RunnerEnv.CreateAsync();
        var stream = env.Subscribe(env.Trader.Id);

        var cycleId = await env.Runner.CloseManuallyAsync(env.Trader,
            new ManualCloseRequest("BTC/USDT", null, "market", null, "手动减仓"), default);
        var received = stream.Drain();

        var cycle = await env.Db.TradingCycles.SingleAsync(c => c.Id == cycleId);
        Assert.Equal("manual", cycle.Trigger);
        Assert.Equal("completed", cycle.Status);
        Assert.Null(cycle.ErrorCode);
        // 演练平仓：只模拟成交，绝不提交交易所（LC-STD-05 TOOL-07 / EXEC-07）。
        Assert.Equal(0, env.Gateway.CloseCallCount);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(cycle.Id, order.CycleId);
        Assert.Equal("close", order.Action);
        Assert.True(order.Simulated);
        Assert.Equal("filled", order.Status);
        Assert.Contains("order", received.Select(e => e.Name));
        Assert.Contains("tool", received.Select(e => e.Name));
        Assert.Equal(2, received.Count(e => e.Name == "cycle"));
    }

    [Fact]
    public async Task Manual_close_rejection_records_execution_errors_without_order()
    {
        await using var env = await RunnerEnv.CreateAsync(positions: []);

        var cycleId = await env.Runner.CloseManuallyAsync(env.Trader,
            new ManualCloseRequest("BTC/USDT", null, "market", null, "手动减仓"), default);

        var cycle = await env.Db.TradingCycles.SingleAsync(c => c.Id == cycleId);
        Assert.Equal("manual", cycle.Trigger);
        Assert.Equal("completed", cycle.Status);
        Assert.Equal("execution_errors", cycle.ErrorCode);
        Assert.Empty(env.Db.TraderOrders);
        Assert.Contains("无该标的持仓", cycle.Error);
    }

    [Fact]
    public async Task Manual_close_with_invalid_quantity_is_rejected_before_the_cycle_starts()
    {
        await using var env = await RunnerEnv.CreateAsync();

        await Assert.ThrowsAsync<FormatException>(() => env.Runner.CloseManuallyAsync(env.Trader,
            new ManualCloseRequest("BTC/USDT", "not-a-number", "market", null, "手动减仓"), default));
        Assert.Equal(0, await env.Db.TradingCycles.CountAsync());
    }

    [Fact]
    public async Task Manual_close_cancellation_still_writes_a_terminal_state()
    {
        // R-11.3 守卫覆盖手动平仓路径：写工具中途被取消，周期绝不能留在 running，
        // 按停止口径记 aborted + cancelled_by_user（LC-STD-01 §8.6）。
        await using var env = await RunnerEnv.CreateAsync(hangPositions: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Runner.CloseManuallyAsync(
            env.Trader, new ManualCloseRequest("BTC/USDT", null, "market", null, "手动减仓"), cts.Token));

        var cycle = await env.Db.TradingCycles.AsNoTracking().SingleAsync();
        Assert.Equal("aborted", cycle.Status);
        Assert.Equal("cancelled_by_user", cycle.ErrorCode);
        Assert.NotNull(cycle.CompletedAt);
        Assert.Empty(env.Db.TraderOrders);
        Assert.Equal(0, env.Gateway.CloseCallCount);
    }

    [Fact]
    public async Task Terminal_write_fault_is_retried_by_the_guard_without_duplicate_cycle_events()
    {
        // 修复轮 2-A1：Finalizer 的终态落库因瞬时 DB 故障抛错时幂等位不得提前置位——宿主的 finally
        // 守卫按 IsTerminal==false 重试终态写入：周期绝不留下 running，且终态 cycle 事件只发一次（LC-STD-01 §11.2）。
        await using var env = await RunnerEnv.CreateAsync(terminalFaultOnce: true);
        var stream = env.Subscribe(env.Trader.Id);

        await env.Runner.RunAsync(env.Trader, default);

        Assert.Equal(1, env.Faulty!.FaultCount);
        var cycle = await env.Db.TradingCycles.AsNoTracking().SingleAsync();
        Assert.NotEqual("running", cycle.Status);
        Assert.NotNull(cycle.CompletedAt);
        var received = stream.Drain();
        // 终态事件恰好一条（开始 + 终态 = 2）：重试路径没有为已落库的终态补发第二个 cycle 事件。
        Assert.Equal(2, received.Count(e => e.Name == "cycle"));
        Assert.Equal(cycle.Status, Json(received[^1]).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Manual_close_while_a_cycle_runs_is_rejected_without_a_cycle_row()
    {
        // 修复轮 2-A2（LC-STD-01 §8.1）：手动平仓与定时周期共用同一交易员周期闸；闸忙不排队，
        // 直接按既有契约码 cycle_in_progress 拒绝且不创建周期行；闸释放后照常执行。
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "hang");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = env.Runner.RunAsync(env.Trader, cts.Token);
        await WaitUntilAsync(() =>
            env.Db.TradingCycles.AsNoTracking().AnyAsync(c => c.Status == "running"));

        var rejected = await Assert.ThrowsAsync<CycleInProgressException>(() => env.Runner.CloseManuallyAsync(
            env.Trader, new ManualCloseRequest("BTC/USDT", null, "market", null, "手动减仓"), default));
        Assert.Equal("cycle_in_progress", rejected.Code);
        // 被拒的平仓没有落任何数据：只剩那一条在跑的定时周期行。
        Assert.Equal(1, await env.Db.TradingCycles.CountAsync());

        cts.Cancel();
        await running;

        var cycleId = await env.Runner.CloseManuallyAsync(env.Trader,
            new ManualCloseRequest("BTC/USDT", null, "market", null, "手动减仓"), default);
        var manual = await env.Db.TradingCycles.AsNoTracking().SingleAsync(c => c.Id == cycleId);
        Assert.Equal("manual", manual.Trigger);
        Assert.Equal("completed", manual.Status);
        Assert.Equal(2, await env.Db.TradingCycles.CountAsync());
    }

    [Fact]
    public async Task Restart_replaces_the_loop_entry_so_the_new_loop_is_still_stoppable()
    {
        // 修复轮 2-A3：Stop+Start（Restart）时濒死循环只允许移除自己的 (traderId, cts) 条目。
        // 无条件 TryRemove 会把刚由 Start 加入的新条目摘走：新循环失控（后续 Stop 成空操作、
        // 其 CTS 无人释放，下一次 Start 再叠一条循环）。身份感知移除下新条目必须原地存活。
        await using var env = await RunnerEnv.CreateAsync(analystBehavior: "hang");
        env.Trader.IsRunning = true;
        await env.Db.SaveChangesAsync();

        try
        {
            env.Runner.Start(env.Trader.Id);
            await WaitUntilAsync(async () =>
                await env.Db.TradingCycles.AsNoTracking().CountAsync() == 1 &&
                await env.Db.TradingCycles.AsNoTracking().AnyAsync(c => c.Status == "running"));
            var dyingCycleId = await env.Db.TradingCycles.AsNoTracking()
                .Select(c => c.Id).SingleAsync();

            env.Runner.Stop(env.Trader.Id);
            env.Runner.Start(env.Trader.Id);

            // 等濒死循环走完收尾（守卫落库终态 → 释放周期闸 → 执行移除语句），再留 100ms 余量：
            await WaitUntilAsync(async () =>
                "aborted" == (await env.Db.TradingCycles.AsNoTracking()
                    .SingleAsync(c => c.Id == dyingCycleId)).Status);
            await Task.Delay(100);

            // 恰好跟踪一次：新循环的条目没有被濒死循环误摘（带 bug 时此处 IsRunning == false）。
            Assert.True(env.Runner.IsRunning(env.Trader.Id));

            env.Runner.Stop(env.Trader.Id);
            Assert.False(env.Runner.IsRunning(env.Trader.Id));
            // Stop 真正生效：新循环若在跑周期必须被取消，任何路径都不留下 running。
            await WaitUntilAsync(async () =>
                !await env.Db.TradingCycles.AsNoTracking().AnyAsync(c => c.Status == "running"));
        }
        finally
        {
            env.Runner.Stop(env.Trader.Id);
        }
    }

    [Fact]
    public async Task Test_run_streams_events_uses_synthetic_equity_and_never_trades()
    {
        await using var env = await RunnerEnv.CreateAsync();
        var received = new List<SseEvent>();

        var cycleId = await env.Runner.RunTestAsync(env.Provider, env.Strategy, 5_000m, false,
            evt => { received.Add(evt); return Task.CompletedTask; }, default);

        var cycle = await env.Db.TradingCycles.SingleAsync(c => c.Id == cycleId);
        Assert.Equal("test", cycle.Trigger);
        Assert.Equal("dry", cycle.Mode);
        Assert.Null(cycle.TraderId);
        Assert.Null(cycle.ExchangeAccountId);
        Assert.Equal("completed", cycle.Status);
        Assert.Equal(0, env.Gateway.CloseCallCount);
        Assert.Equal(0, env.Gateway.PlaceCallCount);
        // 周期开始事件也必须送达（订阅先于周期创建），终态事件恰好一条。
        Assert.Equal(2, received.Count(e => e.Name == "cycle"));
        Assert.Equal("running", Json(received[0]).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Json(received[0]).GetProperty("traderId").ValueKind);
        Assert.Contains("stage", received.Select(e => e.Name));
        // includeMemory=false：记忆与经验一律不读库；权益取合成值进入交易员提示词。
        Assert.Equal(0, env.AccountReader.DecisionMemoryCalls);
        Assert.Contains("权益 5000 USDT", env.Clients.PromptFor("trader"));
    }

    [Fact]
    public async Task Test_run_delivers_write_tool_events_to_on_event()
    {
        // R-11-7：试运行的 tool 事件必须送达 onEvent（订阅的就是本周期 test-run 频道），
        // 绝不能再落到合成交易员的 trader:0 频道。
        await using var env = await RunnerEnv.CreateAsync(executorBehavior: "order");
        var legacyStream = env.Subscribe(TradingCycleRunner.SyntheticTraderChannelId);
        var received = new List<SseEvent>();

        var cycleId = await env.Runner.RunTestAsync(env.Provider, env.Strategy, 5_000m, false,
            evt => { received.Add(evt); return Task.CompletedTask; }, default);

        Assert.Contains("tool", received.Select(e => e.Name));
        var tool = Json(received.First(e => e.Name == "tool"));
        Assert.Equal("place_order", tool.GetProperty("tool").GetString());
        Assert.False(tool.GetProperty("success").GetBoolean());
        Assert.Equal(cycleId, tool.GetProperty("cycleId").GetString());
        // 归属硬闸 fail-closed（API-06）：试运行写调用被拒，不落订单、不触网关，只有审计与事件。
        Assert.Empty(env.Db.TraderOrders);
        Assert.Equal(0, env.Gateway.PlaceCallCount);
        // 回归守卫：trader:0 频道必须收不到任何事件。
        Assert.Empty(legacyStream.Drain());
    }

    [Fact]
    public async Task Write_tool_order_and_tool_events_route_to_the_context_channel()
    {
        // R-11-7：ExecutionTools 的 order 事件与 ToolAuditor 的 tool 事件必须发到 ctx.SseChannel
        // ——试运行语境即 TestRunChannel(cycleId)，也正是 RunTestAsync 里 onEvent 泵送的频道，
        // 不得再硬编码到交易员频道。真实试运行图被周期归属硬闸 fail-closed（见上一个测试，
        // 试运行永远不落订单，API-06），故此处按 ToolRegistry 的装配口径直接装配写工具，
        // 让写路径走到发布点，证明「事件送达频道」而非只断言频道字符串。
        await using var env = await RunnerEnv.CreateAsync();
        var account = await env.Db.ExchangeAccounts.FindAsync(1)
                      ?? throw new InvalidOperationException("夹具缺少交易所账户");
        // 周期行与上下文归属一致（TraderId/Strategy/Account/Mode 同值），让写工具通过归属硬闸。
        env.Db.TradingCycles.Add(new TradingCycle
        {
            Id = env.CycleId, TraderId = env.Trader.Id, StrategyId = env.Trader.StrategyId,
            ProviderId = env.Provider.Id, ExchangeAccountId = account.Id,
            Mode = "dry", Trigger = "test", Status = "running",
            StartedAt = DateTime.UtcNow, StrategyConfigJson = "{}", MemoryFingerprint = "f"
        });
        await env.Db.SaveChangesAsync();

        var stageRunId = Ids.NewUlid();
        var budget = new CycleBudget(env.Config.AgentBudget);
        var market = new MarketDataCache(env.MarketReader);
        var context = new TradingCycleContext(
            cycleId: env.CycleId, trader: env.Trader, config: env.Config, provider: env.Provider,
            account: account, mode: "dry", trigger: "test", exchange: "gate", timeframe: "5m",
            market: market, accounts: env.AccountReader, budget: budget,
            recorder: new CycleRecorder(env.Db, env.Hub));
        Assert.Equal(CycleRecorder.TestRunChannel(env.CycleId), context.SseChannel);

        var (reader, subscription) = env.Hub.Subscribe(context.SseChannel);
        using var _ = subscription;
        var traderStream = env.Subscribe(env.Trader.Id);
        var audit = new ToolAuditor(env.Db, env.Hub, budget, env.CycleId, stageRunId, context.SseChannel);
        var tools = new ExecutionTools(env.Db, context.ForStage(stageRunId), env.AccountReader,
            env.Gateway, market, env.Hub, audit);

        // 演练平仓：模拟成交、落订单行，order 与 tool 事件都必须到达试运行频道。
        var result = await tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null,
            "事件路由回归", default);
        Assert.True(result.Ok, result.Error?.Message);

        var events = new List<SseEvent>();
        while (reader.TryRead(out var evt)) events.Add(evt);
        Assert.Contains("order", events.Select(e => e.Name));
        Assert.Contains("tool", events.Select(e => e.Name));

        var order = Json(events.First(e => e.Name == "order"));
        Assert.Equal(env.CycleId, order.GetProperty("cycleId").GetString());
        Assert.Equal("filled", order.GetProperty("status").GetString());
        Assert.True(order.GetProperty("simulated").GetBoolean());
        var tool = Json(events.First(e => e.Name == "tool"));
        Assert.Equal("close_position", tool.GetProperty("tool").GetString());
        Assert.True(tool.GetProperty("success").GetBoolean());
        // 交易员频道（trader:7）必须收不到本次试运行语境的事件。
        Assert.Empty(traderStream.Drain());
    }

    // —— 夹具 ——

    private static JsonElement Json(SseEvent evt) => JsonSerializer.SerializeToElement(evt.Payload, ApiJson.Options);

    /// <summary>轮询等待条件成立（hosted 服务的 ExecuteAsync 在首个 await 让出后由线程池继续）。</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"等待条件在 {timeoutMs}ms 内未成立");
    }

    private static Provider ScriptedProvider(int contextTokens) => new()
    {
        Id = 1, Name = "脚本提供商", Type = ProviderType.OpenAICompatible,
        BaseUrl = "http://127.0.0.1", Model = "scripted-model",
        ContextTokens = contextTokens, MaxOutputTokens = 16_384
    };

    private sealed class RunnerEnv : IAsyncDisposable
    {
        public required AppDbContext Db { get; init; }
        public required SseEventHub Hub { get; init; }
        public required TradingCycleRunner Runner { get; init; }
        public required Trader Trader { get; init; }
        public required Provider Provider { get; init; }
        public required Strategy Strategy { get; init; }
        public required StrategyConfig Config { get; init; }
        public required ScriptedAccountReader AccountReader { get; init; }
        public required ScriptedMarketReader MarketReader { get; init; }
        public required RecordingGateway Gateway { get; init; }
        public required ScriptedChatClient Clients { get; init; }
        public required IServiceScopeFactory ScopeFactory { get; init; }
        public FaultOnceDbContext? Faulty { get; init; }
        public required string CycleId { get; init; }
        public required string StageRunId { get; init; }

        public static async Task<RunnerEnv> CreateAsync(
            string analystBehavior = "brief", string riskBehavior = "approve",
            string executorBehavior = "clean", string clientFactoryBehavior = "normal",
            int cycleTimeoutSeconds = 300, int riskTimeoutSeconds = 45, List<SymbolPosition>? positions = null,
            bool hangPositions = false, bool terminalFaultOnce = false, string traderBehavior = "proposal",
            int inputTokenBudget = 0)
        {
            // 命名共享缓存内存库 + keeper 连接：宿主恢复/循环写入与测试轮询读取并发进行，
            // 单连接共享模式会直接抛 "database is locked"；共享缓存 + Default Timeout=30
            // 让读写按 SQLite busy 语义排队（与 CycleHttpTests 的宿主同口径）。
            var connectionString =
                $"Data Source=file:runnertest-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=30";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var hub = new SseEventHub();
            var config = new StrategyConfig
            {
                AgentBudget =
                {
                    CycleTimeoutSeconds = cycleTimeoutSeconds,
                    RiskTimeoutSeconds = riskTimeoutSeconds,
                }
            };
            if (inputTokenBudget > 0) config.AgentBudget.InputTokenBudget = inputTokenBudget;
            config.CoinSource.StaticCoins = ["BTC", "ETH", "SOL"];

            var provider = ScriptedProvider(128_000);
            var account = new ExchangeAccount
            {
                Id = 1, ExchangeType = ExchangeType.GateIo, AccountName = "脚本账户", ApiKey = "k", ApiSecret = "s"
            };
            var strategy = new Strategy
            {
                Id = 1, Name = "脚本策略", ConfigJson = JsonSerializer.Serialize(config, VerificationJson.Options)
            };
            var trader = new Trader
            {
                Id = 7, Name = "脚本交易员", ProviderId = 1, ExchangeAccountId = 1, StrategyId = 1,
                ExecutionMode = "dry", ScanIntervalMinutes = 3
            };

            db.Providers.Add(provider);
            db.ExchangeAccounts.Add(account);
            db.Strategies.Add(strategy);
            db.Traders.Add(trader);
            await db.SaveChangesAsync();

            var accountReader = new ScriptedAccountReader(positions ??
                [new SymbolPosition("BTC/USDT", SharedPositionSide.Long, 0.1m, 100m, 1m, 80m, 5m)], hangPositions);
            var marketReader = new ScriptedMarketReader();
            var gateway = new RecordingGateway();
            var clients = new ScriptedChatClient(analystBehavior, riskBehavior, executorBehavior,
                clientFactoryBehavior == "throw", traderBehavior);

            var services = new ServiceCollection();
            // terminalFaultOnce：注入「终态落库失败一次」的瞬时 DB 故障（A1）；scope 解析返回同一实例，
            // 保证宿主的守卫重试与首次写入共用同一个故障上下文。
            FaultOnceDbContext? faulty = terminalFaultOnce ? new FaultOnceDbContext(options) : null;
            if (faulty is not null) services.AddScoped<AppDbContext>(_ => faulty);
            else services.AddScoped(_ => new AppDbContext(options));
            services.AddSingleton(hub);
            services.AddScoped<CycleRecorder>();
            services.AddScoped<ChatClientFactory>(_ => clients);
            var serviceProvider = services.BuildServiceProvider();

            return new RunnerEnv
            {
                Db = db,
                ScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                Hub = hub,
                Runner = new TestRunner(serviceProvider.GetRequiredService<IServiceScopeFactory>(), hub, accountReader, marketReader, gateway),
                Trader = trader,
                Provider = provider,
                Strategy = strategy,
                Config = config,
                AccountReader = accountReader,
                MarketReader = marketReader,
                Gateway = gateway,
                Clients = clients,
                Faulty = faulty,
                CycleId = Ids.NewUlid(),
                StageRunId = Ids.NewUlid(),
                Connection = connection,
            };
        }

        private SqliteConnection Connection { get; init; } = null!;

        public EventStream Subscribe(int traderId) => new(Hub.Subscribe(SseChannels.Trader(traderId)));

        public sealed class EventStream((ChannelReader<SseEvent> Reader, IDisposable Subscription) channel)
        {
            public List<SseEvent> Drain()
            {
                var list = new List<SseEvent>();
                while (channel.Reader.TryRead(out var evt)) list.Add(evt);
                return list;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>
    /// 瞬时 DB 故障注入（修复轮 2-A1）：只在「写终态」那次保存（跟踪项里有非 running 的已修改
    /// TradingCycle）命中时抛一次，之后一律放行——证明宿主的 finally 守卫会重试终态写入。
    /// </summary>
    private sealed class FaultOnceDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public int FaultCount { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FaultCount == 0 && ChangeTracker.Entries<TradingCycle>().Any(entry =>
                    entry.State == EntityState.Modified && entry.Entity.Status != "running"))
            {
                FaultCount++;
                throw new InvalidOperationException("injected-terminal-write-fault");
            }
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>宿主与测试之间的边界缝：账户读、行情读、交易所写网关一律假实现。</summary>
    private sealed class TestRunner(IServiceScopeFactory scopeFactory, SseEventHub hub,
        IAccountReader accounts, IMarketDataReader market, IExecutionGateway gateway)
        : TradingCycleRunner(scopeFactory, hub)
    {
        protected override CycleDataSources CreateDataSources(IServiceProvider services, Trader trader,
            ExchangeAccount account, StrategyConfig config, string memoryFingerprint) =>
            new(accounts, market, gateway);
    }

    /// <summary>按角色工具集识别当前阶段；同一实例同时充当工厂产物（每个周期一个 client）。</summary>
    private sealed class ScriptedChatClient(string analystBehavior, string riskBehavior, string executorBehavior,
        bool throwOnCreate, string traderBehavior = "proposal") : ChatClientFactory, IChatClient
    {
        private readonly Dictionary<string, string> _prompts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _callsByRole = new(StringComparer.Ordinal);

        public bool StageObservedCancellation { get; private set; }

        public override IChatClient Create(Provider provider) =>
            throwOnCreate ? throw new InvalidOperationException("boom-of-upstream 上游炸了") : this;

        public string PromptFor(string role) => _prompts.TryGetValue(role, out var prompt) ? prompt : "";

        /// <summary>该角色真实的模型调用次数（预算闸/契约重试测试据此断言「超限不再发起调用」）。</summary>
        public int CallsFor(string role) => _callsByRole.GetValueOrDefault(role);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var role = DetectRole(options?.Tools);
            if (role is not null && messages.LastOrDefault() is { } user)
            {
                _prompts[role] = user.Text;
            }
            var call = role is null ? 0 : _callsByRole[role] = _callsByRole.GetValueOrDefault(role) + 1;

            var behavior = role switch
            {
                "analyst" => analystBehavior,
                "risk" => riskBehavior,
                "executor" => executorBehavior,
                _ => traderBehavior,
            };

            // 脚本化模型走真实的 ChatClientAgent 工具循环：分析师首轮先调用读工具，再产出结构化结果
            // （LC-STD-05 §3：必须覆盖工具调用、预算与中间件）。
            if (behavior == "brief" && role == "analyst" && call == 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "get_klines",
                        new Dictionary<string, object?> { ["symbol"] = "BTC/USDT", ["timeframe"] = "5m", ["bars"] = 30 })]));
            }

            // overcap：首轮工具调用即回报恰好打满预算的 prompt 用量——第二轮必须被每次调用前的
            // 预算闸拦下、不得转发给本 inner client（LC-STD-01 §6「超限不再发起调用」）。
            if (behavior == "overcap" && role == "analyst" && call == 1)
            {
                var gated = new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-gate", "get_klines",
                        new Dictionary<string, object?> { ["symbol"] = "BTC/USDT", ["timeframe"] = "5m", ["bars"] = 30 })]));
                // 真实提供商在每次响应上都带 usage_metadata；假 client 按同一形状填 Usage。
                gated.Usage = new UsageDetails { InputTokenCount = 2_000, OutputTokenCount = 10 };
                return gated;
            }

            // 「order」：执行员首轮发起一次 place_order 写调用——试运行会被周期归属硬闸拒绝（不落订单、
            // 不触网关），但必须留下写工具审计与 tool 事件；次轮照常产出干净报告（R-11-7 事件流回归）。
            // 写工具入参全部必填（无默认值），一律显式给齐。
            if (behavior == "order" && role == "executor" && call == 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-order", "place_order", new Dictionary<string, object?>
                    {
                        ["proposalId"] = "01PLACEDORDERATTEMPT",
                        ["symbol"] = "BTC/USDT",
                        ["side"] = "long",
                        ["orderType"] = "market",
                        ["quantity"] = 0.01m,
                        ["price"] = null,
                        ["leverage"] = 5m,
                        ["stopLossPrice"] = 95m,
                        ["takeProfitPrice"] = 110m,
                        ["reason"] = "试运行事件流回归",
                    })]));
            }

            switch (behavior)
            {
                case "throw":
                    throw new InvalidOperationException("stage exploded");
                case "hang":
                    try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken); }
                    catch (OperationCanceledException)
                    {
                        StageObservedCancellation = true;
                        throw;
                    }
                    break;
                case "slow":
                    await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                    break;
            }

            var json = behavior switch
            {
                "huge" => HugeBriefJson,
                "errors" => ErrorReportJson,
                _ => PayloadFor(role, behavior, call),
            };
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
            // burst：分析师一次性回报远超预算的输入用量，触发后续阶段的 token 预算闸（FLOW-09）。
            if (behavior == "burst") response.Usage = new UsageDetails { InputTokenCount = 200_000 };
            return response;
        }

        private static string PayloadFor(string? role, string behavior, int call) => role switch
        {
            "analyst" => BriefJson,
            "trader" => behavior switch
            {
                // FLOW-07：越界动作注入——invalid 两轮都越界；invalid-once 首轮越界、重试轮合法。
                "invalid" => InvalidActionJson,
                "invalid-once" when call == 1 => InvalidActionJson,
                _ => OpenProposalJson,
            },
            "risk" when behavior == "reject" => RejectedVerdictJson,
            "risk" => ApprovedVerdictJson,
            "executor" => CleanReportJson,
            _ => BriefJson,
        };

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        /// <summary>阶段身份由角色工具集唯一确定（ToolCatalog 各角色工具互不重叠）。</summary>
        private static string? DetectRole(IList<AITool>? tools)
        {
            if (tools is null) return null;
            var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Contains("get_coin_candidates")) return "analyst";
            if (names.Contains("get_decision_memory")) return "trader";
            if (names.Contains("get_risk_policy")) return "risk";
            if (names.Contains("place_order")) return "executor";
            return null;
        }

        private const string BriefJson =
            """{"marketContext":"BTC 震荡上行","candidates":["BTC/USDT"],"symbols":[],"dataWarnings":[]}""";

        private const string OpenProposalJson =
            """{"action":"open","symbol":"BTC/USDT","side":"long","confidence":0.8,"entryPrice":100,"stopLossPrice":95,"takeProfitPrice":110,"positionPct":1,"leverage":5,"thesis":"趋势向上，回踩确认","invalidation":"跌破 90 失效","reviewChecklist":["检查新鲜度"]}""";

        /// <summary>越界动作（FLOW-07 契约重试注入）：JsonStringEnumConverter 原样接受数值 99。</summary>
        private const string InvalidActionJson =
            """{"action":99,"symbol":"BTC/USDT","side":"long","confidence":0.8,"entryPrice":100,"stopLossPrice":95,"takeProfitPrice":110,"positionPct":1,"leverage":5,"thesis":"越界动作注入","invalidation":null,"reviewChecklist":[]}""";

        private static string ApprovedVerdictJson =>
            $$"""{"decision":"approved","reasons":["边界内"],"maxPositionPct":1,"maxLeverage":5,"entryZoneMin":95,"entryZoneMax":105,"allowMarket":true,"validUntilUtc":"{{DateTime.UtcNow.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture)}}"}""";

        private const string RejectedVerdictJson =
            """{"decision":"rejected","reasons":["行情陈旧"],"maxPositionPct":null,"maxLeverage":null,"entryZoneMin":null,"entryZoneMax":null,"allowMarket":null,"validUntilUtc":null}""";

        private const string CleanReportJson =
            """{"summary":"本轮未提交写请求","attempts":[],"orders":[],"errors":[]}""";

        private const string ErrorReportJson =
            """{"summary":"写工具拒单","attempts":[],"orders":[],"errors":["place_order 拒单：提案未批准"]}""";

        private static string HugeBriefJson =>
            $$"""{"marketContext":"{{new string('盘', 90_000)}}","candidates":["BTC/USDT"],"symbols":[],"dataWarnings":[]}""";
    }

    private sealed class ScriptedAccountReader(List<SymbolPosition> positions, bool hangPositions = false)
        : IAccountReader
    {
        public int DecisionMemoryCalls { get; private set; }

        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) =>
            Task.FromResult(new AccountSnapshot(true, 10_000m, 9_000m, null));

        public async Task<PositionReadResult> GetPositionsAsync(CancellationToken ct)
        {
            // 手动平仓守卫测试：持仓读取中途可取消，模拟写工具在周期落库后被停止。
            if (hangPositions) await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new PositionReadResult(true, positions, null);
        }

        public Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderOrder>>([]);

        public Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<PositionHistory>>([]);

        public Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct)
        {
            DecisionMemoryCalls++;
            return Task.FromResult<IReadOnlyList<TradeProposal>>([]);
        }

        public Task<RiskControlConfig> GetRiskPolicyAsync(CancellationToken ct) =>
            Task.FromResult(new RiskControlConfig());

        public Task<TradingDiscipline> GetDisciplineAsync(CancellationToken ct) =>
            Task.FromResult(new TradingDiscipline(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1),
                0, false, true));

        public Task<IReadOnlyList<TradingCycle>> GetCyclesAsync(DateTime sinceUtc, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TradingCycle>>([]);

        public Task<IReadOnlyList<TraderLesson>> GetLessonsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderLesson>>([]);
    }

    private sealed class ScriptedMarketReader : IMarketDataReader
    {
        public int CallCount { get; private set; }

        public Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
            int bars, int warmupBars, CancellationToken ct)
        {
            CallCount++;
            // 全部为已收盘 K 线（最后一根也在 5 分钟前收口），工具按 ClosedBars 口径可用。
            var lastClose = DateTime.UtcNow.AddMinutes(-5);
            var asOf = DateTime.UtcNow;
            var klines = new List<MarketKline>();
            for (var i = 29; i >= 0; i--)
            {
                var openTime = lastClose - TimeSpan.FromMinutes(5 * i);
                klines.Add(new MarketKline(openTime, 99m, 101m, 98m, 100m, 10m));
            }
            return Task.FromResult(new MarketSnapshot("live", $"{symbol.BaseAsset}/{symbol.QuoteAsset}",
                100m, klines, AsOfUtc: asOf));
        }

        public Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) =>
            Task.FromResult<MarketSnapshot?>(null);

        public Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
            IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, SymbolSentiment>>(new Dictionary<string, SymbolSentiment>());

        public Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(IEnumerable<ExternalDataSource> sources,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalDataResult>>([]);

        public Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct) =>
            Task.FromResult<CoinSourceResolution?>(null);
    }

    /// <summary>写网关：演练路径不应触达，任何调用只计数（用于证明零交易请求）。</summary>
    private sealed class RecordingGateway : IExecutionGateway
    {
        public int PlaceCallCount { get; private set; }
        public int CloseCallCount { get; private set; }

        public Task<decimal?> GetPriceAsync(SharedSymbol symbol, CancellationToken ct) => Task.FromResult<decimal?>(100m);

        public Task<SharedFuturesSymbol?> GetFuturesSpecAsync(SharedSymbol symbol, CancellationToken ct) =>
            Task.FromResult<SharedFuturesSymbol?>(null);

        public Task<(bool Ok, string? Error)> SetLeverageAsync(SharedSymbol symbol, decimal leverage,
            CancellationToken ct) => Task.FromResult((true, (string?)null));

        public Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct) =>
            Task.FromResult<SharedPositionMode?>(SharedPositionMode.OneWay);

        public Task<PlaceResult> PlaceAsync(SharedSymbol symbol, bool isLong, decimal quantity,
            decimal stopLossPrice, decimal? takeProfitPrice, decimal leverage, string orderType, decimal? price,
            string? clientOrderId, CancellationToken ct)
        {
            PlaceCallCount++;
            return Task.FromResult(PlaceResult.NotCreated("测试网关不下单"));
        }

        public Task<CloseResult> CloseAsync(SharedSymbol symbol, decimal? quantity, bool isLong,
            string orderType, decimal? price, string? clientOrderId, CancellationToken ct)
        {
            CloseCallCount++;
            return Task.FromResult(CloseResult.Rejected("测试网关不平仓"));
        }

        public Task<CancelResult> CancelAsync(SharedSymbol symbol, string orderId, CancellationToken ct) =>
            Task.FromResult(CancelResult.Rejected("测试网关不撤单"));

        public TradingAudit Audit() => new(false, false, false, false, false, false, ["测试网关不具备交易能力"]);
    }
}
