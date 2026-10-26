using System.Text.Json;
using CryptoExchange.Net.SharedApis;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Tools;
using LuckyCat.Workflows;
using LuckyCat.Workflows.Executors;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit;

namespace LuckyCat.Tests;

/// <summary>
/// Task 10：四段交易工作流图与六条条件路由（LC-STD-05 FLOW-01~05、FLOW-14）。
/// 全部使用脚本化假 IChatClient 与假 ICycleRecorder / ICycleBudget / IAccountReader / IMarketDataReader，
/// 不依赖任何真实模型；路由错误（不该运行的阶段被运行）会被 ForbiddenClient 直接炸出。
/// </summary>
public class TradingWorkflowTests
{
    private static readonly ResearchBrief BriefWithCandidates = new(
        "BTC 震荡上行，山寨分化", ["BTC/USDT"], [], []);

    private static readonly ResearchBrief BriefNoCandidates = new(
        "候选池为空，无数据", [], [], ["行情源全部缺席"]);

    private static readonly AgentTradeProposal OpenProposal = new(
        ProposalAction.Open, "BTC/USDT", TradeSide.Long, 0.8m,
        100m, 95m, 110m, 1m, 5m, "趋势向上，回踩确认", "跌破 90 失效", null);

    private static readonly AgentTradeProposal HoldProposal = new(
        ProposalAction.Hold, null, null, 0.3m,
        null, null, null, null, null, "信号不足，观望", "候选池出现放量突破", null);

    private static readonly AgentTradeProposal CloseProposal = new(
        ProposalAction.Close, "BTC/USDT", null, 0.7m,
        null, null, null, null, null, "趋势反转，先降风险", "持仓已清空", null);

    private static readonly AgentRiskVerdict ApprovedVerdict = new(
        RiskDecision.Approved, ["边界内，收紧至提案值"],
        1m, 5m, 95m, 105m, true, DateTime.UtcNow.AddMinutes(5));

    private static readonly AgentRiskVerdict RejectedVerdict = new(
        RiskDecision.Rejected, ["信心不足且行情陈旧"],
        null, null, null, null, null, null);

    private static readonly ExecutionReport CleanReport = new(
        "已提交开仓",
        [new ExecutionAttempt("place_order", "{\"symbol\":\"BTC/USDT\"}", "filled", null)],
        [new ExecutionOrder(1, "lc-cycle-abc-1", "filled", 0.1m, 100m)],
        []);

    private static readonly ExecutionReport RejectedWriteReport = new(
        "开仓被写工具拒绝",
        [new ExecutionAttempt("place_order", "{\"symbol\":\"BTC/USDT\"}", "rejected", "proposal_not_approved")],
        [],
        ["place_order 被拒绝：提案未批准"]);

    // —— 六条路由 ——

    [Fact]
    public async Task No_candidates_skips_everything_and_completes_skipped()
    {
        var run = await RunAsync(BriefNoCandidates);

        Assert.Equal("skipped", run.Ctx.Outcome!.Status);
        Assert.Equal(["analyst"], run.Recorder.Stages);
        Assert.Equal(("skipped", (string?)null, (string?)null), run.Recorder.Completions.Single());
        Assert.Equal("skipped", run.Signal.Reason);
        Assert.DoesNotContain("trader", run.Recorder.Stages);
    }

    [Fact]
    public async Task Hold_skips_risk_and_execution()
    {
        var run = await RunAsync(BriefWithCandidates, HoldProposal);

        Assert.Contains("trader", run.Recorder.Stages);
        Assert.DoesNotContain("risk", run.Recorder.Stages);
        Assert.DoesNotContain("executor", run.Recorder.Stages);
        Assert.Equal("completed", run.Ctx.Outcome!.Status);
        Assert.Equal(("completed", (string?)null, (string?)null), run.Recorder.Completions.Single());
        var proposalRecord = run.Recorder.StageRecords.Single(r => r.Role == "trader");
        Assert.Equal(ProposalAction.Hold, Assert.IsType<ProposalMessage>(proposalRecord.Output).Proposal.Action);
    }

    [Fact]
    public async Task Risk_rejected_skips_execution_and_completes()
    {
        var run = await RunAsync(BriefWithCandidates, OpenProposal, RejectedVerdict);

        Assert.Equal(["analyst", "trader", "risk"], run.Recorder.Stages);
        Assert.DoesNotContain("executor", run.Recorder.Stages);
        Assert.Equal("completed", run.Ctx.Outcome!.Status);
        var verdictRecord = run.Recorder.StageRecords.Single(r => r.Role == "risk");
        var message = Assert.IsType<VerdictMessage>(verdictRecord.Output);
        Assert.Equal(RiskDecision.Rejected, message.Verdict.Decision);
        Assert.Equal(OpenProposal.Symbol, message.Proposal.Symbol);
    }

    [Fact]
    public async Task Open_approved_runs_risk_then_execution()
    {
        var run = await RunAsync(BriefWithCandidates, OpenProposal, ApprovedVerdict, CleanReport);

        Assert.Equal(["analyst", "trader", "risk", "executor"], run.Recorder.Stages);
        Assert.Equal("completed", run.Ctx.Outcome!.Status);
        Assert.Null(run.Ctx.Outcome!.ErrorCode);
        Assert.Equal(("completed", (string?)null, (string?)null), run.Recorder.Completions.Single());
        var execution = Assert.IsType<ExecutionReport>(
            run.Recorder.StageRecords.Single(r => r.Role == "executor").Output);
        Assert.Single(execution.Orders);
    }

    [Fact]
    public async Task Close_skips_risk_and_executes_directly()
    {
        var run = await RunAsync(BriefWithCandidates, CloseProposal, verdict: null, report: CleanReport);

        Assert.Equal(["analyst", "trader", "executor"], run.Recorder.Stages);
        Assert.DoesNotContain("risk", run.Recorder.Stages);
        Assert.Equal("completed", run.Ctx.Outcome!.Status);
    }

    [Fact]
    public async Task Write_tool_rejection_completes_with_errors()
    {
        var run = await RunAsync(BriefWithCandidates, OpenProposal, ApprovedVerdict, RejectedWriteReport);

        Assert.Equal(["analyst", "trader", "risk", "executor"], run.Recorder.Stages);
        Assert.Equal("completed", run.Ctx.Outcome!.Status);
        Assert.Equal("execution_errors", run.Ctx.Outcome!.ErrorCode);
        Assert.Contains("提案未批准", run.Ctx.Outcome!.Error);
        Assert.Single(RejectedWriteReport.Errors);
        Assert.Equal(("completed", "execution_errors", run.Ctx.Outcome!.Error), run.Recorder.Completions.Single());
    }

    [Fact]
    public async Task Analyst_stage_applies_symbol_scaling_from_the_brief()
    {
        // 工具额度随候选数放大（LC-STD-01 §6）：分析师拿到实际候选后必须通知预算对象。
        var run = await RunAsync(BriefWithCandidates, HoldProposal);

        var budget = Assert.IsType<FakeBudget>(run.Ctx.Budget);
        Assert.Contains(BriefWithCandidates.Candidates.Count, budget.Scalings);
    }

    // —— 越界枚举契约校验（fail-closed，LC-STD-01 §4.1 第 3 步）——

    [Fact]
    public async Task Out_of_range_confidence_fails_closed_after_one_retry()
    {
        // 实测模型会把 confidence 写成 0–100（如 76.0），契约是 0–1；下游按 ×100 使用，
        // 越界必须在交易员阶段拦下：FLOW-07 重试一次后仍非法 → fail-closed，不进入风控。
        var run = await RunAsync(BriefWithCandidates, OpenProposal with { Confidence = 76m });

        Assert.Equal("failed", run.Ctx.Outcome!.Status);
        Assert.Equal("stage_contract_violation", run.Ctx.Outcome!.ErrorCode);
        Assert.DoesNotContain("risk", run.Recorder.Stages);
        Assert.Equal(2, run.Recorder.StageAttempts[^1]);
        var proposalRecord = run.Recorder.StageRecords.Single(r => r.Role == "trader");
        var safe = Assert.IsType<ProposalMessage>(proposalRecord.Output).Proposal;
        Assert.Equal(ProposalAction.Hold, safe.Action);
        Assert.Null(safe.Confidence);
    }

    [Fact]
    public async Task Out_of_range_proposal_action_fails_closed_via_finalizer()
    {
        // 越界动作被 JsonStringEnumConverter 原样反序列化；risk/executor 装 ForbiddenClient，
        // 一旦运行即炸，证明没有阶段被路由到、更不可能下单。
        var run = await RunAsync(BriefWithCandidates, OpenProposal with { Action = (ProposalAction)99 });

        Assert.Equal("failed", run.Ctx.Outcome!.Status);
        Assert.Equal("stage_contract_violation", run.Ctx.Outcome!.ErrorCode);
        Assert.Equal("failed", run.Signal.Reason);
        Assert.Equal(["analyst", "trader"], run.Recorder.Stages);
        Assert.DoesNotContain("executor", run.Recorder.Stages);
        // 收尾恰运行一次，且带失败终态与错误码。
        var completion = run.Recorder.Completions.Single();
        Assert.Equal(("failed", "stage_contract_violation", run.Ctx.Outcome!.Error), completion);
        Assert.NotNull(run.Ctx.Outcome.Error);
        // trader 出边消息已归一化为 Hold（图中唯一可达 finalizer 的路由）。
        var proposalRecord = run.Recorder.StageRecords.Single(r => r.Role == "trader");
        Assert.Equal(ProposalAction.Hold, Assert.IsType<ProposalMessage>(proposalRecord.Output).Proposal.Action);
    }

    [Fact]
    public async Task Out_of_range_risk_decision_fails_closed_via_finalizer()
    {
        // executor 装 ForbiddenClient：越界裁决若未被归一化为 Rejected 就会死路或误路由。
        var run = await RunAsync(BriefWithCandidates, OpenProposal,
            RejectedVerdict with { Decision = (RiskDecision)99 });

        Assert.Equal("failed", run.Ctx.Outcome!.Status);
        Assert.Equal("stage_contract_violation", run.Ctx.Outcome!.ErrorCode);
        Assert.Equal("failed", run.Signal.Reason);
        Assert.Equal(["analyst", "trader", "risk"], run.Recorder.Stages);
        var completion = run.Recorder.Completions.Single();
        Assert.Equal(("failed", "stage_contract_violation", run.Ctx.Outcome!.Error), completion);
        Assert.NotNull(run.Ctx.Outcome.Error);
        var verdictRecord = run.Recorder.StageRecords.Single(r => r.Role == "risk");
        Assert.Equal(RiskDecision.Rejected,
            Assert.IsType<VerdictMessage>(verdictRecord.Output).Verdict.Decision);
    }

    // —— 阶段落库契约 ——

    [Fact]
    public async Task Each_agent_stage_records_the_prompt_library_system_instructions()
    {
        var run = await RunAsync(BriefWithCandidates, OpenProposal, ApprovedVerdict, CleanReport);

        foreach (var (role, agentRole) in new[]
                 {
                     ("analyst", AgentRole.Analyst),
                     ("trader", AgentRole.Trader),
                     ("risk", AgentRole.Risk),
                     ("executor", AgentRole.Executor),
                 })
        {
            var record = run.Recorder.StageRecords.Single(r => r.Role == role);
            Assert.Equal(PromptLibrary.Build(agentRole, run.Config), record.Instructions);
        }
    }

    // —— ToolRegistry 与工具绑定逐字一致（字面量交叉校验，刻意不复用 ToolCatalog 数组）——

    [Theory]
    [InlineData(AgentRole.Analyst)]
    [InlineData(AgentRole.Trader)]
    [InlineData(AgentRole.Risk)]
    [InlineData(AgentRole.Executor)]
    public async Task ToolRegistry_exposes_catalog_names_verbatim_per_role(AgentRole role)
    {
        await using var env = await TestEnv.CreateAsync();

        var tools = env.Registry.For(role, "stage-run-1");
        var expected = ExpectedToolBindings(role);

        // 名称与顺序钉死为测试内字面量：若 ToolCatalog 数组被改动，本测试红。
        Assert.Equal(expected.Select(b => b.Name), tools.Select(tool => tool.Name));
        Assert.Equal(expected.Length, tools.Count);

        // 名称↔底层方法绑定同样钉死为字面量：若注册处两行 Fn(...) 连索引一起对调，
        // 名称集合不变但配对断言必红（仅比名称数组抓不到这种交换）。
        Assert.Equal(expected.Select(b => b.Method),
            tools.Select(tool => Assert.IsAssignableFrom<AIFunction>(tool).UnderlyingMethod?.Name));
    }

    [Fact]
    public async Task Reviewer_tools_assemble_only_through_the_static_entry()
    {
        // LC-STD-03 §2.1：复盘工具调用 cycleId 必须为 null，而 For 会把周期的非空 cycleId 写进
        // 审计器——For(Reviewer) 已在注册位结构性拒绝，唯一装配位是 BuildReviewerTools。
        // 绑定逐字校验与上方 Theory 同一口径（审计器显式传 cycleId: null）。
        await using var env = await TestEnv.CreateAsync();

        var rejected = Assert.Throws<InvalidOperationException>(
            () => env.Registry.For(AgentRole.Reviewer, "stage-run-1"));
        Assert.Contains(nameof(ToolRegistry.BuildReviewerTools), rejected.Message);

        var audit = new ToolAuditor(env.Db, new SseEventHub(), env.Ctx.Budget, cycleId: null,
            "stage-run-1", env.Ctx.SseChannel);
        var tools = ToolRegistry.BuildReviewerTools(env.Ctx.Accounts, audit);
        var expected = ExpectedToolBindings(AgentRole.Reviewer);

        Assert.Equal(expected.Select(b => b.Name), tools.Select(tool => tool.Name));
        Assert.Equal(expected.Length, tools.Count);
        Assert.Equal(expected.Select(b => b.Method),
            tools.Select(tool => Assert.IsAssignableFrom<AIFunction>(tool).UnderlyingMethod?.Name));
    }

    /// <summary>字面量副本：名字逐字抄自 ToolCatalog、方法名逐字抄自 ToolRegistry 的 Fn 绑定，
    /// 两边同时被改才会一起绿——这正是交叉校验的意义。</summary>
    private static (string Name, string Method)[] ExpectedToolBindings(AgentRole role) => role switch
    {
        AgentRole.Analyst =>
        [
            ("get_coin_candidates", "GetCoinCandidatesAsync"),
            ("get_market_overview", "GetMarketOverviewAsync"),
            ("get_klines", "GetKlinesAsync"),
            ("get_indicators", "GetIndicatorsAsync"),
            ("get_sentiment", "GetSentimentAsync"),
            ("get_news", "GetNewsAsync"),
        ],
        AgentRole.Trader =>
        [
            ("get_klines", "GetKlinesAsync"),
            ("get_indicators", "GetIndicatorsAsync"),
            ("get_sentiment", "GetSentimentAsync"),
            ("get_account", "GetAccountAsync"),
            ("get_positions", "GetPositionsAsync"),
            ("get_recent_orders", "GetRecentOrdersAsync"),
            ("get_closed_trades", "GetClosedTradesAsync"),
            ("get_decision_memory", "GetDecisionMemoryAsync"),
            ("get_lessons", "GetLessonsAsync"),
            ("get_trading_discipline", "GetTradingDisciplineAsync"),
        ],
        AgentRole.Risk =>
        [
            ("get_account", "GetAccountAsync"),
            ("get_positions", "GetPositionsAsync"),
            ("get_risk_policy", "GetRiskPolicyAsync"),
            ("get_trading_discipline", "GetTradingDisciplineAsync"),
        ],
        AgentRole.Executor =>
        [
            ("get_account", "GetAccountAsync"),
            ("get_positions", "GetPositionsAsync"),
            ("get_recent_orders", "GetRecentOrdersAsync"),
            ("place_order", "PlaceOrderAsync"),
            ("close_position", "ClosePositionAsync"),
            ("cancel_order", "CancelOrderAsync"),
        ],
        AgentRole.Reviewer =>
        [
            ("get_closed_trades", "GetClosedTradesAsync"),
            ("get_cycles", "GetCyclesAsync"),
            ("get_lessons", "GetLessonsAsync"),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    // —— 执行器装配 ——

    private sealed record RunResult(CycleSignal Signal, TradingCycleContext Ctx, FakeRecorder Recorder, StrategyConfig Config);

    private static async Task<RunResult> RunAsync(ResearchBrief brief, AgentTradeProposal? proposal = null,
        AgentRiskVerdict? verdict = null, ExecutionReport? report = null)
    {
        await using var env = await TestEnv.CreateAsync();

        var workflow = env.BuildWorkflow(
            new JsonClient(brief),
            proposal is null ? new ForbiddenClient("trader") : new JsonClient(proposal),
            verdict is null ? new ForbiddenClient("risk") : new JsonClient(verdict),
            report is null ? new ForbiddenClient("executor") : new JsonClient(report));

        var run = await InProcessExecution.RunAsync(workflow, new CycleInput(env.Ctx.CycleId));
        var signal = Assert.Single(run.NewEvents.OfType<WorkflowOutputEvent>()
            .Select(evt => evt.Data).OfType<CycleSignal>());

        return new RunResult(signal, env.Ctx, env.Recorder, env.Config);
    }

    private sealed class TestEnv : IAsyncDisposable
    {
        public required TradingCycleContext Ctx { get; init; }
        public required FakeRecorder Recorder { get; init; }
        public required StrategyConfig Config { get; init; }
        public required ToolRegistry Registry { get; init; }
        public required AppDbContext Db { get; init; }
        public required SqliteConnection Connection { get; init; }

        public static async Task<TestEnv> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var config = new StrategyConfig();
            config.CoinSource.StaticCoins = ["BTC", "ETH", "SOL"];
            var recorder = new FakeRecorder();
            var market = new MarketDataCache(new EmptyMarketReader());
            var ctx = new TradingCycleContext(
                cycleId: Ids.NewUlid(),
                trader: new Trader { Id = 7, Name = "测试交易员", ProviderId = 1, ExchangeAccountId = 1, StrategyId = 3 },
                config: config,
                provider: new Provider { Id = 1, Name = "p", Type = ProviderType.OpenAICompatible, BaseUrl = "http://127.0.0.1", Model = "m" },
                account: new ExchangeAccount { Id = 1, ExchangeType = ExchangeType.GateIo, AccountName = "a", ApiKey = "k", ApiSecret = "s" },
                mode: "dry",
                trigger: "test",
                exchange: "gate",
                timeframe: "5m",
                market: market,
                accounts: new FakeAccountReader(),
                budget: new FakeBudget(),
                recorder: recorder);

            var registry = new ToolRegistry(ctx, db, new ForbiddenGateway(), new SseEventHub(),
                new HashSet<string>(StringComparer.Ordinal) { "BTC/USDT", "ETH/USDT", "SOL/USDT" });

            return new TestEnv
            {
                Ctx = ctx,
                Recorder = recorder,
                Config = config,
                Registry = registry,
                Db = db,
                Connection = connection,
            };
        }

        public Workflow BuildWorkflow(IChatClient analystClient, IChatClient traderClient,
            IChatClient riskClient, IChatClient executorClient)
        {
            var factory = new TradingAgentFactory();
            var runner = new AgentRunner();
            var analyst = new AnalystExecutor(Ctx, factory, runner, Registry, analystClient);
            var trader = new TraderExecutor(Ctx, factory, runner, Registry, traderClient);
            var risk = new RiskExecutor(Ctx, factory, runner, Registry, riskClient);
            var executor = new ExecutionExecutor(Ctx, factory, runner, Registry, executorClient);
            var finalizer = new FinalizerExecutor(Ctx);
            return TradingCycleWorkflow.Build(Ctx, analyst, trader, risk, executor, finalizer);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    // —— 假执行记录 / 预算 ——

    private sealed class FakeRecorder : ICycleRecorder
    {
        public List<string> Stages { get; } = [];
        public List<(string Role, object Output, string Instructions)> StageRecords { get; } = [];
        public List<int> StageAttempts { get; } = [];
        public List<(string Status, string? ErrorCode, string? Error)> Completions { get; } = [];

        public Task RecordStageAsync(string role, string stageRunId, object output, string instructions,
            StageRunStats stats, int attempt, CancellationToken ct)
        {
            Stages.Add(role);
            StageRecords.Add((role, output, instructions));
            StageAttempts.Add(attempt);
            return Task.CompletedTask;
        }

        public Task CompleteCycleAsync(TradingCycleContext ctx, string status, string? errorCode,
            string? error, CancellationToken ct)
        {
            Completions.Add((status, errorCode, error));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBudget : ICycleBudget
    {
        public int Consumed { get; private set; }
        public List<int> Scalings { get; } = [];

        public bool TryConsumeTool(string role)
        {
            Consumed++;
            return true;
        }

        public void ApplySymbolScaling(int symbolCount) => Scalings.Add(symbolCount);
    }

    // —— 假模型：固定返回一个结构化 JSON，不做工具调用 ——

    private sealed class JsonClient(object payload) : IChatClient
    {
        private readonly string _json = JsonSerializer.Serialize(payload, AgentJson.Options);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _json)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>不该被运行的阶段装上它：一旦被调用即测试失败，证明路由正确。</summary>
    private sealed class ForbiddenClient(string stage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"stage '{stage}' must not run on this route");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // —— 假数据源：提示词拼接会读取它们，工具在脚本化测试里不会被模型调用 ——

    private sealed class EmptyMarketReader : IMarketDataReader
    {
        public Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
            int bars, int warmupBars, CancellationToken ct) =>
            Task.FromResult(new MarketSnapshot("missing", symbol.BaseAsset, null, [], Failure: "测试行情不可用"));

        public Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) =>
            Task.FromResult<MarketSnapshot?>(null);

        public Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
            IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, SymbolSentiment>>(new Dictionary<string, SymbolSentiment>());

        public Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExternalDataResult>>([]);

        public Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct) =>
            Task.FromResult<CoinSourceResolution?>(null);
    }

    private sealed class FakeAccountReader : IAccountReader
    {
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) =>
            Task.FromResult(new AccountSnapshot(true, 10_000m, 9_000m, null));

        public Task<PositionReadResult> GetPositionsAsync(CancellationToken ct) =>
            Task.FromResult(new PositionReadResult(true,
                [new SymbolPosition("BTC/USDT", SharedPositionSide.Long, 0.1m, 100m, 1m, 80m, 5m)], null));

        public Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderOrder>>([]);

        public Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PositionHistory>>([]);

        public Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TradeProposal>>([]);

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

    /// <summary>图路由测试不触发真实写请求：任何网关调用都直接抛错。</summary>
    private sealed class ForbiddenGateway : IExecutionGateway
    {
        private const string Message = "gateway must not be called by routing tests";

        public Task<decimal?> GetPriceAsync(SharedSymbol symbol, CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<SharedFuturesSymbol?> GetFuturesSpecAsync(SharedSymbol symbol, CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<(bool Ok, string? Error)> SetLeverageAsync(SharedSymbol symbol, decimal leverage,
            CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<PlaceResult> PlaceAsync(SharedSymbol symbol, bool isLong, decimal quantity,
            decimal stopLossPrice, decimal? takeProfitPrice, decimal leverage, string orderType, decimal? price,
            string? clientOrderId, CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<CloseResult> CloseAsync(SharedSymbol symbol, decimal? quantity, bool isLong,
            string orderType, decimal? price, string? clientOrderId, CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<CancelResult> CancelAsync(SharedSymbol symbol, string orderId, CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public TradingAudit Audit() => throw new InvalidOperationException(Message);
    }
}
