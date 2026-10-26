using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.Extensions.AI;

namespace LuckyCat.Workflows;

/// <summary>
/// 阶段与周期落库抽象（Task 11 实现：AgentStageRun 原文落库、
/// 终态落库与 SSE 推送按 <see cref="CycleEvents.cs"/> 的负载契约发布）。
/// 收尾执行器不调用模型、不产生 AgentStageRun，只调用 CompleteCycleAsync（LC-STD-01 §4.1）。
/// </summary>
public interface ICycleRecorder
{
    /// <summary>
    /// 记录一个角色阶段。<paramref name="stageRunId"/> **必须**是执行器自身的
    /// <c>StageRunId</c>：ToolAuditor 已按同一 ID 落 ToolInvocation，宿主另造 ID 会让工具审计行悬空。
    /// <paramref name="attempt"/> 是阶段内的模型尝试次数（LC-STD-05 FLOW-07 契约重试），从 1 起，
    /// 非重试阶段一律传 1。
    /// </summary>
    Task RecordStageAsync(string role, string stageRunId, object output, string instructions,
        StageRunStats stats, int attempt, CancellationToken ct);

    Task CompleteCycleAsync(TradingCycleContext ctx, string status, string? errorCode, string? error,
        CancellationToken ct);
}

/// <summary>
/// 阶段观测数据（LC-STD-03 §2.1 AgentStageRun 的用量/耗时/消息记录来源）：
/// 由阶段执行器在模型调用处测量，由周期宿主的落库记录器写入并截断。
/// </summary>
/// <param name="Messages">本轮模型可见的消息（含工具调用记录；可为空）。</param>
/// <param name="ElapsedMs">阶段耗时（模型循环 + 工具循环），毫秒。</param>
/// <param name="InputTokens">输入 token 用量（上游未回报时为 null）。</param>
/// <param name="OutputTokens">输出 token 用量（上游未回报时为 null）。</param>
/// <param name="Reasoning">思维链摘要，≤2,000 字。</param>
public sealed record StageRunStats(
    IReadOnlyList<ChatMessage> Messages,
    long ElapsedMs,
    int? InputTokens,
    int? OutputTokens,
    string? Reasoning)
{
    /// <summary>思维链落库上限（AgentStageRun.Reasoning 字段长度，LC-STD-03 §2.1）。</summary>
    public const int MaxReasoningChars = 2000;

    public static StageRunStats From(IList<ChatMessage>? messages, UsageDetails? usage, long elapsedMs)
    {
        var list = messages?.ToList() ?? [];
        var reasoning = string.Join("\n", list
            .SelectMany(message => message.Contents)
            .OfType<TextReasoningContent>()
            .Select(content => content.Text))
            .Trim();
        return new StageRunStats(list, elapsedMs, ToInt(usage?.InputTokenCount), ToInt(usage?.OutputTokenCount),
            reasoning.Length == 0 ? null : reasoning[..Math.Min(reasoning.Length, MaxReasoningChars)]);
    }

    /// <summary>空观测（未调用模型的阶段，如收尾或失败阶段）。</summary>
    public static StageRunStats Empty { get; } = new([], 0, null, null, null);

    private static int? ToInt(long? value) => value is < int.MinValue or > int.MaxValue
        ? null
        : value.HasValue ? (int)value.Value : null;
}

/// <summary>阶段超时事实（阶段名、实际耗时、配置的秒数），由执行器在超时令牌触发时写入上下文。</summary>
public sealed record StageTimeoutFailure(string Stage, long ElapsedMs, int TimeoutSeconds);

/// <summary>
/// 单周期作用域上下文（LC-STD-01 §4.2）：持有交易员、策略快照、提供商、账户、模式、触发来源、
/// 行情缓存、账户读取器、工具预算与落库记录器。不得包含可变静态状态，不同周期之间不共享。
/// 阶段执行器把已产出的简报/提案/裁决回存到本上下文，供 <see cref="BuildUserPrompt"/> 与后续阶段读取。
/// </summary>
public sealed class TradingCycleContext(
    string cycleId, Trader trader, StrategyConfig config, Provider provider,
    ExchangeAccount account, string mode, string trigger, string exchange, string timeframe,
    MarketDataCache market, IAccountReader accounts, ICycleBudget budget, ICycleRecorder recorder)
{
    public string CycleId { get; } = cycleId;
    public Trader Trader { get; } = trader;
    public StrategyConfig Config { get; } = config;
    public Provider Provider { get; } = provider;
    public ExchangeAccount Account { get; } = account;
    public string Mode { get; } = mode;
    public string Trigger { get; } = trigger;
    public string Exchange { get; } = exchange;
    public string Timeframe { get; } = timeframe;
    public MarketDataCache Market { get; } = market;
    public IAccountReader Accounts { get; } = accounts;
    public ICycleBudget Budget { get; } = budget;
    public ICycleRecorder Recorder { get; } = recorder;

    /// <summary>本轮研究简报（交易员阶段的用户提示词输入）。</summary>
    public ResearchBrief? Brief { get; private set; }

    /// <summary>本轮交易提案（风控/执行阶段的用户提示词输入）。</summary>
    public AgentTradeProposal? Proposal { get; private set; }

    /// <summary>本轮风控裁决（执行阶段开仓路径的用户提示词输入）。</summary>
    public AgentRiskVerdict? Verdict { get; private set; }

    /// <summary>周期终态（阶段写入、Finalizer 读取）。</summary>
    public CycleOutcome? Outcome { get; private set; }

    /// <summary>本周期第一个撞上自身阶段超时的阶段；仅供宿主归因 stage_timeout，不影响路由。</summary>
    public StageTimeoutFailure? TimedOutStage { get; private set; }

    public void SetOutcome(CycleOutcome outcome) => Outcome = outcome;

    /// <summary>执行器在阶段超时令牌触发时写入真实耗时；用户/周期级取消不得调用。</summary>
    public void MarkStageTimeout(string stage, long elapsedMs) =>
        TimedOutStage = new StageTimeoutFailure(stage, elapsedMs, Budget.StageTimeoutSeconds(stage));

    /// <summary>
    /// SSE 频道的单点决策（R-11-7）：试运行按周期独立成频道（<see cref="CycleRecorder.TestRunChannel"/>），
    /// 其余触发一律 <see cref="SseChannels.Trader(int)"/>。周期落库（CycleRecorder）、工具审计
    /// （ToolAuditor）与写工具订单事件（ExecutionTools，经 <see cref="ForStage"/> 线程化）共用本属性，
    /// 任何发布者都不得自行拼频道键。
    /// </summary>
    public string SseChannel => Trigger == "test"
        ? CycleRecorder.TestRunChannel(CycleId)
        : SseChannels.Trader(Trader.Id);

    /// <summary>写工具阶段上下文：与 <see cref="ExecutionToolContext"/> 契约逐字段对齐（Task 7）。</summary>
    public ExecutionToolContext ForStage(string stageRunId) =>
        new(CycleId, stageRunId, Mode, Exchange, Timeframe, Trader, Account, Config, Budget, SseChannel);

    /// <summary>
    /// 阶段超时缝（LC-STD-01 §6）：返回与调用 token 链接的超时源，秒数取本周期预算的阶段口径；
    /// 预算未配置阶段超时（&lt;=0）时只链接、不定时。超时经 <see cref="CancellationToken"/> 传播，
    /// 不得用 <see cref="Task.WhenAny"/> 遗留未取消的任务（调用方负责 dispose）。
    /// </summary>
    public CancellationTokenSource StageTimeout(CancellationToken ct, string stage)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var seconds = Budget.StageTimeoutSeconds(stage);
        if (seconds > 0) source.CancelAfter(TimeSpan.FromSeconds(seconds));
        return source;
    }

    /// <summary>
    /// 输入 token 预算闸门（LC-STD-01 §6、LC-STD-05 FLOW-09）：本周期累计输入用量达到预算即超限，
    /// 后续阶段一律不再发起模型调用，周期按「观望」完成且不下单。
    /// </summary>
    public bool TokenBudgetExhausted() => Budget.IsInputTokenBudgetExceeded(Provider);

    /// <summary>阶段执行器回存研究简报。</summary>
    internal void Observe(ResearchBrief brief) => Brief = brief;

    /// <summary>阶段执行器回存交易提案。</summary>
    internal void Observe(AgentTradeProposal proposal) => Proposal = proposal;

    /// <summary>阶段执行器回存风控裁决。</summary>
    internal void Observe(AgentRiskVerdict verdict) => Verdict = verdict;

    /// <summary>
    /// 按角色构建用户提示词（LC-STD-04 §2 最小必要：分析师只看行情、风控/执行看提案与账户、
    /// 执行员的平仓路径无裁决）。复盘员提示词属于 Task 13。
    /// </summary>
    public Task<string> BuildUserPrompt(AgentRole role, CancellationToken ct) => role switch
    {
        AgentRole.Analyst => PromptLibrary.BuildAnalystUserPrompt(this, ct),
        AgentRole.Trader => PromptLibrary.BuildTraderUserPrompt(this,
            Brief ?? throw new InvalidOperationException("研究员简报尚未回存，无法构建交易员提示词"), ct),
        AgentRole.Risk => PromptLibrary.BuildRiskUserPrompt(this,
            Proposal ?? throw new InvalidOperationException("交易提案尚未回存，无法构建风控提示词"), ct),
        AgentRole.Executor => PromptLibrary.BuildExecutorUserPrompt(this,
            Proposal ?? throw new InvalidOperationException("交易提案尚未回存，无法构建执行提示词"), Verdict, ct),
        _ => throw new NotSupportedException($"角色 {role} 的用户提示词不在本图范围内"),
    };
}
