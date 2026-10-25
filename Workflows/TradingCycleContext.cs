using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Tools;

namespace LuckyCat.Workflows;

/// <summary>
/// 阶段与周期落库抽象（周期宿主在 Task 11 实现：AgentStageRun 原文落库、
/// 终态落库与 SSE 推送按 <see cref="CycleEvents.cs"/> 的负载契约发布）。
/// 收尾执行器不调用模型、不产生 AgentStageRun，只调用 CompleteCycleAsync（LC-STD-01 §4.1）。
/// </summary>
public interface ICycleRecorder
{
    Task RecordStageAsync(string role, object output, string instructions, CancellationToken ct);

    Task CompleteCycleAsync(TradingCycleContext ctx, string status, string? errorCode, string? error,
        CancellationToken ct);
}

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

    public void SetOutcome(CycleOutcome outcome) => Outcome = outcome;

    /// <summary>写工具阶段上下文：与 <see cref="ExecutionToolContext"/> 契约逐字段对齐（Task 7）。</summary>
    public ExecutionToolContext ForStage(string stageRunId) =>
        new(CycleId, stageRunId, Mode, Exchange, Timeframe, Trader, Account, Config, Budget);

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
