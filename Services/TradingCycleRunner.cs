using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Tools;
using LuckyCat.Workflows;
using LuckyCat.Workflows.Executors;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace LuckyCat.Services;

/// <summary>
/// 周期边界实现装配缝（LC-STD-05 §3）：单周期使用的账户只读、行情只读与交易所写网关。
/// 生产实现按交易员绑定的账户装配；测试与试运行以假实现替换，业务代码只看到接口。
/// </summary>
public sealed record CycleDataSources(IAccountReader Accounts, IMarketDataReader MarketReader, IExecutionGateway Gateway);

/// <summary>
/// 周期闸冲突（LC-STD-03:505/§交易员错误码表：<c>cycle_in_progress</c>，409「已有周期」）：
/// 同一交易员已有周期在运行时手动平仓被拒绝，<b>不创建任何周期行</b>。
/// 端点层（Task 14）按本类型映射 409 + 契约错误码。
/// </summary>
public sealed class CycleInProgressException(string message) : Exception(message)
{
    /// <summary>契约错误码（既有约定，LC-STD-03）。</summary>
    public string Code => "cycle_in_progress";
}

/// <summary>
/// 周期宿主（LC-STD-01 §4/§6/§8）：每个交易员一个独立调度循环，一个周期 = 一条 <c>running</c>
/// <see cref="TradingCycle"/> + 一次 <see cref="TradingCycleWorkflow"/> 图执行 + 一个终态。
/// <para>
/// 职责边界：阶段执行器只做模型与路由，落库与 SSE 一律经 <see cref="CycleRecorder"/>；
/// 本类负责预算与超时（单周期总超时以 <see cref="CancellationToken"/> 传播到阶段内部，不用
/// <see cref="Task.WhenAny"/> 遗留未取消任务）、同交易员并发闸（同一时刻最多一个周期）、
/// 终态保证（<c>finally</c> 守卫：任何路径都不留下 <c>running</c>，且不覆盖 Finalizer 已写的终态）、
/// 重启恢复（遗留 <c>running</c> → <c>aborted/process_restart</c>）与手动平仓（复用写工具硬闸）。
/// </para>
/// <para>
/// 依赖注入按本仓库既有后台服务口径：<see cref="IServiceScopeFactory"/> + 单例
/// <see cref="SseEventHub"/>，<see cref="AppDbContext"/>、<see cref="CycleRecorder"/>、
/// <see cref="ChatClientFactory"/> 等都从「每周期一个 scope」解析，避免单例捕获 scoped 依赖。
/// </para>
/// <para>
/// 迁移期调度边界（R-11-6）：本类是可解析的单例周期引擎，但<b>不是</b> hosted 调度——
/// 旧 <see cref="TraderRunnerService"/> 仍是唯一自动恢复运行中交易员的调度链，本类若同时注册为
/// hosted 服务会造成双循环双开仓。调度循环只经 <see cref="Start(int)"/> 显式启动（Task 14 端点、
/// Task 18 切流后的自动恢复），启动恢复由 <see cref="CycleStartupRecovery"/> 单独承担；
/// Task 18 删除旧链路时再把本类重新注册为 hosted 调度并恢复循环，<see cref="RunLoopAsync"/>
/// 的循环机制保留备用，勿删。
/// </para>
/// </summary>
public class TradingCycleRunner(IServiceScopeFactory scopeFactory, SseEventHub hub)
{
    /// <summary>试运行合成交易员的 SSE 频道 ID（周期与事件都发往该频道）。</summary>
    public const int SyntheticTraderChannelId = 0;

    // —— 周期错误码（LC-STD-03 §2.1：cancelled_by_user / process_restart / risk_stage_failed 为既有约定，
    //    cycle_timeout 与 Finalizer 的 execution_errors / missing_outcome 复用既有码，
    //    stage_failed 与 cycle_internal_error 为本轮补充的通用失败码）——
    internal const string RiskStageFailedCode = "risk_stage_failed";
    internal const string StageFailedCode = "stage_failed";
    internal const string CycleTimeoutCode = "cycle_timeout";
    internal const string CancelledByUserCode = "cancelled_by_user";
    internal const string ProcessRestartCode = "process_restart";
    internal const string MissingOutcomeCode = "missing_outcome";
    internal const string InternalErrorCode = "cycle_internal_error";
    internal const string ExecutionErrorsCode = "execution_errors";
    internal const string TokenBudgetExhaustedCode = "token_budget_exceeded";

    private const int MinIntervalMinutes = 3;
    private const int MaxIntervalMinutes = 60;

    private readonly ConcurrentDictionary<int, CancellationTokenSource> _loops = new();
    private readonly ConcurrentDictionary<int, string> _abortReasons = new();
    // 同一交易员同时最多一个运行中周期（LC-STD-01 §8.1）；账户写锁在 ExecutionTools 内，不在此重复。
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _cycleGates = new();

    /// <summary>该交易员的调度循环是否在运行。</summary>
    public bool IsRunning(int traderId) => _loops.ContainsKey(traderId);

    /// <summary>
    /// 启动交易员调度循环（首个周期立即执行；已在运行则幂等）。迁移期只有显式调用才会启动——
    /// 本类不注册 hosted 调度（R-11-6），调用方是 Task 14 的端点与 Task 18 的切流恢复。
    /// </summary>
    public void Start(int traderId)
    {
        if (_loops.ContainsKey(traderId)) return;
        var cts = new CancellationTokenSource();
        if (_loops.TryAdd(traderId, cts))
        {
            // 不在这里清 _abortReasons：Start 紧随 Stop 时（Restart），濒死周期可能还没读完归因码，
            // 提前清除会让它误归为默认码。中止码由 Stop 写入、由拥有该条循环条目的路径清理（见 RunLoopAsync）。
            _ = Task.Run(() => RunLoopAsync(traderId, cts), cts.Token);
        }
        else
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// 停止交易员：取消该交易员当前周期的 CancellationToken（LC-STD-01 §8.6），
    /// 周期随后记录 <c>aborted</c>。默认中止码为 <c>cancelled_by_user</c>；
    /// 应用停机路径传 <c>process_restart</c>，与重启恢复同一口径。
    /// </summary>
    public void Stop(int traderId, string abortReasonCode = CancelledByUserCode)
    {
        if (_loops.TryRemove(traderId, out var cts))
        {
            _abortReasons[traderId] = abortReasonCode;
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>编辑运行中的交易员后调用：以新配置重启循环。</summary>
    public void Restart(int traderId)
    {
        if (!IsRunning(traderId)) return;
        Stop(traderId);
        Start(traderId);
    }

    /// <summary>
    /// 启动恢复：把所有遗留 <c>running</c> 周期标记 <c>aborted</c> + <c>process_restart</c>
    /// （LC-STD-01 §8.7、LC-STD-05 FLOW-13），返回恢复的周期数。已落库的阶段保留不动。
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var recovered = await db.TradingCycles
            .Where(cycle => cycle.Status == "running")
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(cycle => cycle.Status, "aborted")
                    .SetProperty(cycle => cycle.ErrorCode, ProcessRestartCode)
                    .SetProperty(cycle => cycle.Error, "进程重启，周期未完成即中止")
                    .SetProperty(cycle => cycle.CompletedAt, now),
                ct);
        if (recovered > 0)
        {
            // 看板与 state 事件的 lastCycleStatus 同步收口，避免交易员侧残留「running」。
            await db.Traders.Where(trader => trader.LastCycleStatus == "running")
                .ExecuteUpdateAsync(setters => setters.SetProperty(trader => trader.LastCycleStatus, "aborted"), ct);
            Console.WriteLine($"[Cycle] 重启恢复：{recovered} 个遗留 running 周期已标记 aborted");
        }
        return recovered;
    }

    /// <summary>
    /// 运行一个定时周期（trigger=schedule）。同一交易员已有周期在跑时直接跳过并返回 null
    /// （不排队、不并行，LC-STD-01 §8.1）；正常返回周期 ID。
    /// </summary>
    public async Task<string?> RunAsync(Trader trader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trader);
        var gate = _cycleGates.GetOrAdd(trader.Id, _ => new SemaphoreSlim(1, 1));
        if (await gate.WaitAsync(0, CancellationToken.None))
        {
            try
            {
                return await RunScheduledCycleAsync(trader, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        Console.WriteLine($"[Cycle] 交易员 #{trader.Id} 已有周期在跑，本轮跳过");
        return null;
    }

    /// <summary>
    /// 手动平仓（EXEC-07）：创建 <c>trigger=manual</c> 周期，复用 <see cref="ExecutionTools.ClosePositionAsync"/>
    /// 的完整写工具硬闸与审计，随后完成周期并返回周期 ID。
    /// 数量/价格按契约以字符串传入，非法值在创建周期之前抛 <see cref="FormatException"/>（不落任何数据）。
    /// 与定时周期共用同一交易员周期闸（LC-STD-01 §8.1「任一周期同时最多一个」）：闸忙时不排队，
    /// 直接抛 <see cref="CycleInProgressException"/>（<c>cycle_in_progress</c>，409 语义），不创建周期行。
    /// </summary>
    public async Task<string> CloseManuallyAsync(Trader trader, ManualCloseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trader);
        ArgumentNullException.ThrowIfNull(request);
        var quantity = ParseOptionalDecimal(request.Quantity, nameof(request.Quantity));
        var price = ParseOptionalDecimal(request.Price, nameof(request.Price));

        // 周期闸（与 RunAsync 同一把）：WaitAsync(0) 不排队；任何路径退出都必须释放。
        var gate = _cycleGates.GetOrAdd(trader.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, CancellationToken.None))
        {
            Console.WriteLine($"[Cycle] 交易员 #{trader.Id} 已有周期在跑，手动平仓被拒绝（cycle_in_progress）");
            throw new CycleInProgressException($"交易员 #{trader.Id} 已有周期在运行，手动平仓被拒绝");
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var cycle = await PrepareCycleAsync(scope, trader, "manual", ct);

            // 手动平仓没有 AI 阶段：直接装配执行员写工具（与 ToolRegistry 同一件装配口径）。
            var stageRunId = Ids.NewUlid();
            var audit = new ToolAuditor(cycle.Db, hub, cycle.Context.Budget, cycle.Context.CycleId, stageRunId,
                cycle.Context.SseChannel);
            var tools = new ExecutionTools(cycle.Db, cycle.Context.ForStage(stageRunId), cycle.Sources.Accounts,
                cycle.Sources.Gateway, cycle.Market, hub, audit);
            try
            {
                var result = await tools.ClosePositionAsync(null, request.Symbol, quantity, request.OrderType,
                    price, request.Reason, ct);
                await cycle.Recorder.CompleteCycleAsync(cycle.Context, "completed",
                    result.Ok ? null : ExecutionErrorsCode,
                    result.Ok ? null : result.Error?.Message, CancellationToken.None);
            }
            finally
            {
                // 终态守卫（与定时周期同一口径，LC-STD-01 §11.2）：写工具取消或抛错都不允许把周期留在
                // running；CompleteCycleAsync 幂等，正常完成路径这里是空操作。
                if (!cycle.Recorder.IsTerminal)
                {
                    var aborted = ct.IsCancellationRequested;
                    await cycle.Recorder.CompleteCycleAsync(cycle.Context,
                        aborted ? "aborted" : "failed",
                        aborted ? CancelledByUserCode : InternalErrorCode,
                        aborted ? "用户停止，手动平仓已中止" : "手动平仓异常，本周期已按失败安全终止",
                        CancellationToken.None);
                }
            }

            return cycle.Context.CycleId;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 试运行（API-06 / MEM-07）：以合成账户、<c>trigger=test</c>、<c>mode=dry</c> 跑真实图，
    /// 不落交易员与真实账户（周期行 TraderId/ExchangeAccountId 为 null），事件经 <paramref name="onEvent"/>
    /// 回写给调用方（test-run 的 SSE 响应流）。行情只读允许，交易与账户接口一律不访问。
    /// <paramref name="includeMemory"/>=false 时记忆与经验完全不读库。
    /// </summary>
    public async Task<string> RunTestAsync(Provider provider, Strategy strategy, decimal testEquity,
        bool includeMemory, Func<SseEvent, Task>? onEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(strategy);

        using var scope = scopeFactory.CreateScope();
        var cycleId = Ids.NewUlid();
        var trader = new Trader
        {
            Id = SyntheticTraderChannelId, Name = "试运行", ProviderId = provider.Id,
            ExchangeAccountId = 0, StrategyId = strategy.Id, ExecutionMode = "dry"
        };

        // 先订阅再创建周期：周期开始事件也必须送达（试运行按周期 ID 独立成频道，并发试运行互不串流）。
        var (reader, subscription) = hub.Subscribe(CycleRecorder.TestRunChannel(cycleId));
        using var _ = subscription;
        var isPumping = onEvent is not null;
        var pumping = isPumping ? PumpEventsAsync(reader, onEvent!, ct) : Task.CompletedTask;
        var cycle = await PrepareCycleAsync(scope, trader, "test", ct,
            strategy, provider, testEquity, includeMemory, cycleId);
        try
        {
            await ExecuteCycleAsync(cycle, ct);
        }
        finally
        {
            subscription.Dispose();
            if (isPumping) await pumping;
        }

        return cycle.Context.CycleId;
    }

    /// <summary>装配本周期使用的账户/行情/写网关边界实现（测试与试运行的注入缝）。</summary>
    protected virtual CycleDataSources CreateDataSources(IServiceProvider services, Trader trader,
        ExchangeAccount account, StrategyConfig config, string memoryFingerprint)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var trading = services.GetRequiredService<ExchangeTradingService>();
        var reader = new AccountReader(db, trading,
            new AccountReaderScope(account, trader.Id, trader.StrategyId, config, memoryFingerprint));
        var market = new MarketDataReader(services.GetRequiredService<MarketSnapshotService>(),
            services.GetRequiredService<MarketSentimentService>(),
            services.GetRequiredService<ExternalDataSourceService>(),
            services.GetRequiredService<CoinRankingService>());
        return new CycleDataSources(reader, market,
            new ExecutionGateway(account, trading, trader.IsCrossMargin));
    }

    private async Task RunLoopAsync(int traderId, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var intervalMinutes = MinIntervalMinutes;
            try
            {
                Trader? trader;
                using (var scope = scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    trader = await db.Traders.FindAsync([traderId], ct);
                }

                if (trader is null || !trader.IsRunning)
                {
                    Stop(traderId, trader is null ? ProcessRestartCode : CancelledByUserCode);
                    break;
                }

                intervalMinutes = trader.ScanIntervalMinutes;
                await RunAsync(trader, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogException($"trader:{traderId} 周期异常", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(
                    Math.Clamp(intervalMinutes, MinIntervalMinutes, MaxIntervalMinutes)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 身份感知移除：只摘除本循环自己的 (traderId, cts) 条目。Stop+Start（Restart）后字典里
        // 可能已换成新循环的 CTS——无条件 TryRemove 会误摘新条目，让新循环失控且其 CTS 无人释放。
        // CTS 的释放归「移除并持有它」的路径（Stop 移除即取消并释放）；这里只清归属该循环的中止码。
        if (((ICollection<KeyValuePair<int, CancellationTokenSource>>)_loops)
                .Remove(new KeyValuePair<int, CancellationTokenSource>(traderId, cts)))
        {
            _abortReasons.TryRemove(traderId, out _);
        }
    }

    private async Task<string> RunScheduledCycleAsync(Trader trader, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var cycle = await PrepareCycleAsync(scope, trader, "schedule", ct);
        await ExecuteCycleAsync(cycle, ct);
        return cycle.Context.CycleId;
    }

    /// <summary>
    /// 周期准备：读交易员绑定的策略/提供商/账户 → 装配上下文 → 先落库 <c>running</c> 周期
    /// （LC-STD-01 §8.2「周期开始前必须创建并提交」）。任何缺失都在这里抛出，此时还没有周期行可遗留。
    /// </summary>
    private async Task<CycleSession> PrepareCycleAsync(IServiceScope scope, Trader trader, string trigger,
        CancellationToken ct, Strategy? strategyOverride = null, Provider? providerOverride = null,
        decimal? syntheticEquity = null, bool includeMemory = true, string? cycleId = null)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await LoadConfigAsync(db, trader, strategyOverride, ct);
        var provider = providerOverride
            ?? await db.Providers.FindAsync([trader.ProviderId], ct)
            ?? throw new InvalidOperationException("模型提供商不存在");
        var account = trigger == "test"
            ? SyntheticAccount()
            : await db.ExchangeAccounts.FindAsync([trader.ExchangeAccountId], ct)
              ?? throw new InvalidOperationException("交易所账户不存在");

        var fingerprint = PromptLibrary.MemoryFingerprint(config);
        var sources = CreateDataSources(scope.ServiceProvider, trader, account, config, fingerprint);
        if (syntheticEquity is { } equity)
        {
            sources = sources with { Accounts = new SyntheticAccountReader(sources.Accounts, equity, includeMemory) };
        }

        var market = new MarketDataCache(sources.MarketReader);
        var budget = new CycleBudget(config.AgentBudget);
        var recorder = scope.ServiceProvider.GetRequiredService<CycleRecorder>();
        var context = new TradingCycleContext(
            cycleId: cycleId ?? Ids.NewUlid(),
            trader: trader,
            config: config,
            provider: provider,
            account: account,
            mode: trigger == "test" ? "dry" : trader.ExecutionMode,
            trigger: trigger,
            exchange: config.CoinSource.Exchange,
            timeframe: config.Indicators.Klines.PrimaryTimeframe,
            market: market,
            accounts: sources.Accounts,
            budget: budget,
            recorder: recorder);

        await recorder.StartCycleAsync(context, ct);
        return new CycleSession(db, scope.ServiceProvider, context, sources, market, budget, recorder);
    }

    /// <summary>执行图、预算/超时与终态保证（LC-STD-01 §4.1 末条、§11.2）。</summary>
    private async Task ExecuteCycleAsync(CycleSession cycle, CancellationToken ct)
    {
        var ctx = cycle.Context;
        var state = new CycleRunState();
        using var activity = Telemetry.Source.StartActivity("trader.cycle");
        activity?.SetTag("trader.id", ctx.Trader.Id);
        activity?.SetTag("cycle.id", ctx.CycleId);
        activity?.SetTag("cycle.trigger", ctx.Trigger);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(cycle.Budget.CycleTimeoutSeconds));
        try
        {
            await RunGraphAsync(cycle, timeout.Token, state);
        }
        catch (OperationCanceledException)
        {
            // 超时或用户停止：终态一律由下面的守卫按取消来源分类写出。
        }
        catch (Exception ex)
        {
            LogException($"cycle:{ctx.CycleId} 宿主异常", ex);
            state.HostFailure = new StageFailure(InternalErrorCode, "周期宿主异常，本周期已按失败安全终止");
        }
        finally
        {
            try
            {
                await EnsureTerminalStateAsync(cycle, ct, timeout, state);
            }
            catch (Exception ex)
            {
                // 终态写入本身失败（数据库不可用等）：只能记录并上抛给调度循环，绝不在这里静默。
                LogException($"cycle:{ctx.CycleId} 终态写入失败", ex);
                throw;
            }
            activity?.SetTag("cycle.status", cycle.Recorder.TerminalStatus ?? "unknown");
            Telemetry.Cycles.Add(1,
                new KeyValuePair<string, object?>("status", cycle.Recorder.TerminalStatus ?? "unknown"),
                new KeyValuePair<string, object?>("trigger", ctx.Trigger));
        }
    }

    private async Task RunGraphAsync(CycleSession cycle, CancellationToken runToken, CycleRunState state)
    {
        var ctx = cycle.Context;
        var allowed = await BuildAllowedSymbolsAsync(cycle.Market, ctx.Config, runToken);
        var registry = new ToolRegistry(ctx, cycle.Db, cycle.Sources.Gateway, hub, allowed);
        var factory = new TradingAgentFactory();
        var runner = new AgentRunner();
        // token 预算每调用闸（LC-STD-01 §6「预算在每次模型调用前累计检查，超限不再发起调用」）：
        // 图内所有模型调用（含阶段工具循环的续跑）都经由该装饰器；装饰器负责释放上游 client。
        var rawClient = cycle.Services.GetRequiredService<ChatClientFactory>().Create(ctx.Provider);
        using var client = new TokenBudgetGatedChatClient(rawClient, cycle.Budget, ctx.Provider);

        var analyst = new AnalystExecutor(ctx, factory, runner, registry, client);
        var trader = new TraderExecutor(ctx, factory, runner, registry, client);
        var risk = new RiskExecutor(ctx, factory, runner, registry, client);
        var executor = new ExecutionExecutor(ctx, factory, runner, registry, client);
        var finalizer = new FinalizerExecutor(ctx);
        var slots = new Dictionary<string, StageSlot>(StringComparer.Ordinal)
        {
            ["analyst"] = new("analyst", analyst.StageRunId),
            ["trader"] = new("trader", trader.StageRunId),
            ["risk"] = new("risk", risk.StageRunId),
            ["executor"] = new("executor", executor.StageRunId),
        };

        var workflow = TradingCycleWorkflow.Build(ctx, analyst, trader, risk, executor, finalizer);
        await using var run = await InProcessExecution.RunStreamingAsync<CycleInput>(
            workflow, new CycleInput(ctx.CycleId), null, runToken);
        await foreach (var evt in run.WatchStreamAsync(runToken))
        {
            await ConsumeEventAsync(evt, ctx, cycle.Recorder, slots, state);
        }
    }

    /// <summary>
    /// 事件消费：阶段的落库与 SSE 由 <see cref="CycleRecorder"/> 负责，这里只补宿主侧的两件事——
    /// ① 阶段抛错时按执行器 ID 对齐 StageRunId 写「失败阶段」并归因错误码（fail-closed）；
    /// ② 图错误/终态输出记 trace 与服务端日志。异常本身不在此重述（原始文本不外泄）。
    /// </summary>
    private static async Task ConsumeEventAsync(WorkflowEvent evt, TradingCycleContext ctx, CycleRecorder recorder,
        IReadOnlyDictionary<string, StageSlot> slots, CycleRunState state)
    {
        switch (evt)
        {
            case ExecutorFailedEvent failed:
                // 预算闸拒绝（每次模型调用前的检查）归因到既有 token 码；其余沿用阶段/风控失败码。
                var refused = IsTokenBudgetRefusal(failed.Data);
                var code = refused
                    ? TokenBudgetExhaustedCode
                    : failed.ExecutorId == "risk" ? RiskStageFailedCode : StageFailedCode;
                var summary = refused
                    ? "输入 token 预算已达上限，不再发起模型调用，本周期已按失败安全终止"
                    : failed.ExecutorId == "risk"
                        ? "风控阶段执行失败，本周期已按失败安全终止（不下单）"
                        : $"{failed.ExecutorId} 阶段执行失败，本周期已按失败安全终止";
                state.StageFailure ??= new StageFailure(code, summary);
                if (slots.TryGetValue(failed.ExecutorId, out var slot))
                {
                    await recorder.RecordStageFailureAsync(slot.Role, slot.StageRunId, code, summary,
                        CancellationToken.None);
                }
                LogException($"cycle:{ctx.CycleId} stage:{failed.ExecutorId}", failed.Data);
                break;
            case WorkflowErrorEvent errored:
                state.HostFailure ??= new StageFailure(InternalErrorCode, "工作流执行异常，本周期已按失败安全终止");
                LogException($"cycle:{ctx.CycleId} workflow", errored.Exception);
                break;
            case WorkflowOutputEvent { Data: CycleSignal signal }:
                Console.WriteLine($"[Cycle] cycle:{ctx.CycleId} 图输出终态 {signal.Reason}");
                break;
        }
    }

    private async Task EnsureTerminalStateAsync(CycleSession cycle, CancellationToken ct,
        CancellationTokenSource timeout, CycleRunState state)
    {
        var recorder = cycle.Recorder;
        if (recorder.IsTerminal) return;

        var ctx = cycle.Context;
        if (ct.IsCancellationRequested)
        {
            var code = _abortReasons.TryGetValue(ctx.Trader.Id, out var reason) ? reason : CancelledByUserCode;
            await recorder.CompleteCycleAsync(ctx, "aborted", code,
                code == ProcessRestartCode ? "应用停止，本周期已中止" : "用户停止，本周期已中止", CancellationToken.None);
            return;
        }

        if (timeout.IsCancellationRequested)
        {
            await recorder.CompleteCycleAsync(ctx, "failed", CycleTimeoutCode,
                "周期超过总超时，剩余阶段已取消", CancellationToken.None);
            return;
        }

        // 阶段失败是根因，优先于其后的宿主异常归类。
        var failure = state.StageFailure ?? state.HostFailure;
        await recorder.CompleteCycleAsync(ctx, "failed",
            failure?.Code ?? MissingOutcomeCode,
            failure?.Summary ?? "图未产出终态，本周期按失败安全记录", CancellationToken.None);
    }

    /// <summary>
    /// 把频道事件转交调用方回调；订阅释放（频道关闭）或调用方取消都正常收尾，
    /// 不让取消盖掉周期自身的终态结果。
    /// </summary>
    private static Task PumpEventsAsync(ChannelReader<SseEvent> reader, Func<SseEvent, Task> onEvent,
        CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                await onEvent(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：事件不再送达即可，周期本身已按约定落库。
        }
    }, CancellationToken.None);

    private static async Task<StrategyConfig> LoadConfigAsync(AppDbContext db, Trader trader,
        Strategy? strategyOverride, CancellationToken ct)
    {
        var strategy = strategyOverride ?? await db.Strategies.FindAsync([trader.StrategyId], ct);
        if (strategy is null) throw new InvalidOperationException("策略不存在");
        return JsonSerializer.Deserialize<StrategyConfig>(strategy.ConfigJson, VerificationJson.Options)
               ?? new StrategyConfig();
    }

    private static ExchangeAccount SyntheticAccount() => new()
    {
        Id = 0,
        ExchangeType = ExchangeType.Binance,
        AccountName = "试运行合成账户",
        // 合成账户不带任何凭据：试运行只跑只读行情，写工具与账户接口一律不触达（API-06）。
        ApiKey = "",
        ApiSecret = "",
    };

    /// <summary>候选池 ∪ 静态币（大写 BASE/QUOTE），剔除排除币：写工具与行情工具的允许集合上界。</summary>
    private static async Task<IReadOnlySet<string>> BuildAllowedSymbolsAsync(MarketDataCache market,
        StrategyConfig config, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var coin in config.CoinSource.StaticCoins) AddSymbol(set, coin);
        var resolution = await market.GetCandidatesAsync(config, ct);
        if (resolution is not null)
        {
            foreach (var symbol in resolution.Merged) AddSymbol(set, $"{symbol.BaseAsset}/{symbol.QuoteAsset}");
        }
        foreach (var coin in config.CoinSource.ExcludedCoins)
        {
            set.Remove(NormalizedSymbol(coin));
        }
        return set;

        static void AddSymbol(HashSet<string> set, string coin) => set.Add(NormalizedSymbol(coin));
    }

    private static string NormalizedSymbol(string coin)
    {
        var shared = CoinRankingService.ParseConfiguredSymbol(coin);
        return $"{shared.BaseAsset}/{shared.QuoteAsset}";
    }

    private static decimal? ParseOptionalDecimal(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException($"{field} 必须是十进制数字字符串");
        }
        return parsed;
    }

    private static void LogException(string context, Exception? ex)
    {
        // 上游异常原文只进服务端日志，绝不进入落库数据或 SSE 负载（R-11-1、LC-STD-02 §10）；
        // 进日志前同样脱敏——异常文本可能自带含密钥的 URL（LC-STD-04 §8）。
        if (ex is null) return;
        Console.WriteLine($"[Cycle] {context}：{LogRedaction.Scrub(ex.ToString())}");
    }

    private sealed record StageFailure(string Code, string Summary);

    /// <summary>
    /// token 预算闸拒绝的内部信号：只在图内出现（<see cref="TokenBudgetGatedChatClient"/> 抛出，
    /// 由 <see cref="ConsumeEventAsync"/> 归因为 <see cref="TokenBudgetExhaustedCode"/>），
    /// 不逃逸出图、不携带任何上游原始异常文本。
    /// </summary>
    private sealed class TokenBudgetRejectedException : Exception
    {
        public TokenBudgetRejectedException() : base("token budget gate refused the model call") { }
    }

    /// <summary>
    /// 每次模型调用前的 token 预算闸（LC-STD-01 §6「预算在每次模型调用前累计检查，超限不再发起调用」）。
    /// 装配位：<see cref="RunGraphAsync"/> 包住本周期唯一的 <see cref="ChatClientFactory"/> 产物，
    /// 阶段工具循环的每一轮续跑都经过这里。发起委托前双检，任一命中即拒绝且<b>不向上游转发</b>：
    /// ① 周期累计输入已达上限——与阶段入口闸（<see cref="ICycleBudget.IsInputTokenBudgetExceeded"/>）
    ///    同一口径，覆盖每阶段第一次调用；② 上一轮 PromptTokens 已达上限——工具循环里 prompt 只增，
    ///    上一轮 prompt 是下一轮 prompt 的保守下界预测。
    /// 用量累计的唯一装配位也在本装饰器（每次原始调用后 <c>AddTokens</c>）；
    /// <see cref="CycleRecorder"/> 阶段收尾时不再向周期预算累计，避免与逐调用累计双重计数。
    /// 拒绝走既有阶段失败路径（ExecutorFailedEvent → 阶段 failed → 周期 failed），不新增逃逸异常类型。
    /// </summary>
    private sealed class TokenBudgetGatedChatClient(IChatClient inner, CycleBudget budget, Provider provider)
        : IChatClient
    {
        /// <summary>上一轮（含此前所有轮次，prompt 单调不减）的 PromptTokens 预测值。</summary>
        private int _predictedPromptTokens;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            RefuseIfExhausted();
            var response = await inner.GetResponseAsync(messages, options, cancellationToken);

            var inputTokens = ToInt(response.Usage?.InputTokenCount);
            budget.AddTokens(inputTokens, ToInt(response.Usage?.OutputTokenCount));
            // 下一轮 prompt ≥ 上一轮 prompt（工具循环只追加消息）：取历史最大值做保守预测。
            _predictedPromptTokens = Math.Max(_predictedPromptTokens, inputTokens);
            return response;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            // 本图只走非流式路径（ChatClientAgent 工具循环）；流式同样先过闸，超限不再发起调用。
            RefuseIfExhausted();
            return inner.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey is null && serviceType == typeof(IChatClient)) return this;
            return inner.GetService(serviceType, serviceKey);
        }

        public void Dispose() => inner.Dispose();

        private void RefuseIfExhausted()
        {
            var limit = budget.InputTokenBudget(provider);
            if (_predictedPromptTokens >= limit || budget.IsInputTokenBudgetExceeded(provider))
            {
                throw new TokenBudgetRejectedException();
            }
        }

        private static int ToInt(long? value) => value is null or < 0
            ? 0
            : value > int.MaxValue ? int.MaxValue : (int)value.Value;
    }

    /// <summary>沿 InnerException 链识别预算闸拒绝（中间件可能原样包裹，也可能逐层包装）。</summary>
    private static bool IsTokenBudgetRefusal(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TokenBudgetRejectedException) return true;
            if (current.InnerException is null) return false;
        }
        return false;
    }

    /// <summary>本周期一次图运行的可变观测状态（阶段失败归因、图错误标记）。</summary>
    private sealed class CycleRunState
    {
        public StageFailure? StageFailure { get; set; }
        public StageFailure? HostFailure { get; set; }
    }

    /// <summary>已准备好的周期：同一 scope 内的数据库、上下文、边界实现与记录器。</summary>
    private sealed class CycleSession(
        AppDbContext db,
        IServiceProvider services,
        TradingCycleContext context,
        CycleDataSources sources,
        MarketDataCache market,
        CycleBudget budget,
        CycleRecorder recorder)
    {
        public AppDbContext Db { get; } = db;
        public IServiceProvider Services { get; } = services;
        public TradingCycleContext Context { get; } = context;
        public CycleDataSources Sources { get; } = sources;
        public MarketDataCache Market { get; } = market;
        public CycleBudget Budget { get; } = budget;
        public CycleRecorder Recorder { get; } = recorder;
    }

    /// <summary>执行器 ID → 阶段标识与 StageRunId（阶段失败时按此对齐 AgentStageRun）。</summary>
    private sealed record StageSlot(string Role, string StageRunId);

    /// <summary>
    /// 试运行账户读取器：权益固定为合成值、持仓为空，绝不触达交易/账户接口（API-06）；
    /// includeMemory=false 时记忆、经验、订单与平仓历史完全不读库。
    /// </summary>
    private sealed class SyntheticAccountReader(IAccountReader inner, decimal equity, bool includeMemory)
        : IAccountReader
    {
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) =>
            Task.FromResult(new AccountSnapshot(true, equity, equity, null));

        public Task<PositionReadResult> GetPositionsAsync(CancellationToken ct) =>
            Task.FromResult(new PositionReadResult(true, [], null));

        public Task<RiskControlConfig> GetRiskPolicyAsync(CancellationToken ct) => inner.GetRiskPolicyAsync(ct);

        public Task<TradingDiscipline> GetDisciplineAsync(CancellationToken ct) => inner.GetDisciplineAsync(ct);

        public Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct) =>
            includeMemory ? inner.GetRecentOrdersAsync(limit, ct) : Task.FromResult<IReadOnlyList<TraderOrder>>([]);

        public Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct) => includeMemory
            ? inner.GetClosedTradesAsync(sinceUtc, limit, ct)
            : Task.FromResult<IReadOnlyList<PositionHistory>>([]);

        public Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct) =>
            includeMemory
                ? inner.GetDecisionMemoryAsync(limit, ct)
                : Task.FromResult<IReadOnlyList<TradeProposal>>([]);

        public Task<IReadOnlyList<TradingCycle>> GetCyclesAsync(DateTime sinceUtc, int limit, CancellationToken ct) =>
            includeMemory
                ? inner.GetCyclesAsync(sinceUtc, limit, ct)
                : Task.FromResult<IReadOnlyList<TradingCycle>>([]);

        public Task<IReadOnlyList<TraderLesson>> GetLessonsAsync(int limit, CancellationToken ct) => includeMemory
            ? inner.GetLessonsAsync(limit, ct)
            : Task.FromResult<IReadOnlyList<TraderLesson>>([]);
    }
}
