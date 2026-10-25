using LuckyCat.Contracts;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>
/// 试运行账户读取器（Task 11 内嵌实现的原样提取，Task 14 提升为独立类型供端点与测试复用）：
/// 权益固定为合成值、持仓为空，绝不触达交易/账户接口（LC-STD-05 API-06）；
/// <paramref name="includeMemory"/>=false 时记忆、经验、订单与平仓历史完全不读库。
/// 风控策略与交易纪律一律委托给 inner 真实读取器——本类型不伪造任何它无法知道的数据（LC-STD-04 安全块）。
/// </summary>
public sealed class SyntheticAccountReader(IAccountReader inner, decimal equity, bool includeMemory)
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
