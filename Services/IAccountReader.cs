using LuckyCat.Contracts;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>
/// 提案记忆条目：提案本体 + 对应风控裁决（仅 open）+ 关联执行订单。
/// 读取器按 <c>(strategyId, traderId, memoryFingerprint)</c> 完全匹配，避免跨策略/跨指纹注入。
/// </summary>
public sealed record DecisionMemoryEntry(
    TradeProposal Proposal,
    RiskVerdict? Verdict,
    IReadOnlyList<TraderOrder> Executions);

/// <summary>
/// 单周期账户只读接口：所有方法都绑定构造时的账户/交易员/策略/指纹，不接受调用方传入归属参数，
/// 从类型层面阻断模型跨账户、跨交易员读取（IDOR）。金额均为 USDT，时间为 UTC。
/// </summary>
public interface IAccountReader
{
    Task<AccountSnapshot> GetAccountAsync(CancellationToken ct);
    Task<PositionReadResult> GetPositionsAsync(CancellationToken ct);
    Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit, CancellationToken ct);
    Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct);
    Task<RiskControlConfig> GetRiskPolicyAsync(CancellationToken ct);
    Task<TradingDiscipline> GetDisciplineAsync(CancellationToken ct);
    Task<IReadOnlyList<TradingCycle>> GetCyclesAsync(DateTime sinceUtc, int limit, CancellationToken ct);
    Task<IReadOnlyList<TraderLesson>> GetLessonsAsync(int limit, CancellationToken ct);

    /// <summary>
    /// 提案记忆明细（含 open 裁决与执行结果）。默认实现只返回提案，便于合成/测试读取器复用；
    /// 真实读取器覆盖本方法以附带裁决与订单。
    /// </summary>
    public async Task<IReadOnlyList<DecisionMemoryEntry>> GetDecisionMemoryDetailsAsync(int limit, CancellationToken ct)
    {
        var proposals = await GetDecisionMemoryAsync(limit, ct);
        return proposals.Select(proposal => new DecisionMemoryEntry(proposal, null, [])).ToArray();
    }
}
