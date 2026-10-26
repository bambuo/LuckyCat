using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using Microsoft.EntityFrameworkCore;

namespace LuckyCat.Services;

/// <summary>
/// 单周期读取器绑定：账户、交易员、策略与记忆指纹。由周期宿主在构造 <see cref="AccountReader"/> 时提供，
/// 读取器内部所有查询都以这些字段为强制过滤条件；模型无法通过工具参数改变归属。
/// </summary>
public sealed record AccountReaderScope(
    ExchangeAccount Account,
    int TraderId,
    int StrategyId,
    StrategyConfig Config,
    string MemoryFingerprint,
    TimeZoneInfo? TimeZone = null)
{
    /// <summary>纪律窗口所用时区；默认服务器本地时区（与旧执行链路一致）。</summary>
    public TimeZoneInfo Zone => TimeZone ?? TimeZoneInfo.Local;
}

/// <summary>
/// 账户只读实现：账户/持仓走 <see cref="ExchangeTradingService"/> 的实时读取；
/// 订单、平仓历史、提案记忆、周期、经验按绑定的账户/交易员/策略/指纹从数据库过滤。
/// 只返回绑定范围内的数据，绝不返回交易所密钥或原始上游错误。
/// </summary>
public sealed class AccountReader(
    AppDbContext db,
    ExchangeTradingService trading,
    AccountReaderScope scope,
    Func<DateTime>? clock = null) : IAccountReader
{
    private const int RecentOrderLimit = 50;
    private const int ClosedTradeLimit = 50;
    private const int DecisionMemoryLimit = 8;
    private const int CycleLimit = 20;
    private const int LessonLimit = 3;

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) =>
        trading.GetFreshAccountAsync(scope.Account, ct);

    public Task<PositionReadResult> GetPositionsAsync(CancellationToken ct) =>
        trading.GetPositionSnapshotAsync(scope.Account, ct);

    public async Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct) =>
        await db.TraderOrders.AsNoTracking()
            .Where(order => order.TraderId == scope.TraderId)
            .OrderByDescending(order => order.At)
            .ThenByDescending(order => order.Id)
            .Take(Math.Clamp(limit, 1, RecentOrderLimit))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit,
        CancellationToken ct)
    {
        var since = AsUtc(sinceUtc);
        return await db.PositionHistories.AsNoTracking()
            .Where(trade => trade.ExchangeAccountId == scope.Account.Id && trade.CloseTime >= since)
            .OrderByDescending(trade => trade.CloseTime)
            .ThenByDescending(trade => trade.Id)
            .Take(Math.Clamp(limit, 1, ClosedTradeLimit))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct) =>
        await MemoryQuery()
            .OrderByDescending(proposal => proposal.CreatedAt)
            .ThenByDescending(proposal => proposal.Id)
            .Take(Math.Clamp(limit, 1, DecisionMemoryLimit))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DecisionMemoryEntry>> GetDecisionMemoryDetailsAsync(int limit,
        CancellationToken ct)
    {
        var proposals = await GetDecisionMemoryAsync(limit, ct);
        if (proposals.Count == 0) return [];

        var ids = proposals.Select(proposal => proposal.Id).ToList();
        var openIds = proposals
            .Where(proposal => proposal.Action == "open")
            .Select(proposal => proposal.Id)
            .ToList();

        var verdicts = openIds.Count == 0
            ? new Dictionary<string, RiskVerdict>(StringComparer.Ordinal)
            : await db.RiskVerdicts.AsNoTracking()
                .Where(verdict => openIds.Contains(verdict.ProposalId))
                .ToDictionaryAsync(verdict => verdict.ProposalId, StringComparer.Ordinal, ct);

        var executions = await db.TraderOrders.AsNoTracking()
            .Where(order => order.TraderId == scope.TraderId && order.ProposalId != null && ids.Contains(order.ProposalId))
            .OrderBy(order => order.At)
            .ThenBy(order => order.Id)
            .ToListAsync(ct);
        var executionsByProposal = executions
            .Where(order => order.ProposalId is not null)
            .GroupBy(order => order.ProposalId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<TraderOrder>)group.ToList(), StringComparer.Ordinal);

        return proposals.Select(proposal => new DecisionMemoryEntry(
            proposal,
            proposal.Action == "open" ? verdicts.GetValueOrDefault(proposal.Id) : null,
            executionsByProposal.GetValueOrDefault(proposal.Id) ?? [])).ToList();
    }

    public Task<RiskControlConfig> GetRiskPolicyAsync(CancellationToken ct) =>
        Task.FromResult(scope.Config.RiskControl);

    public async Task<TradingDiscipline> GetDisciplineAsync(CancellationToken ct)
    {
        var now = AsUtc(_clock());
        var zone = scope.Zone;
        var dayStart = TradingRiskPolicy.EvaluateDiscipline([], now, zone).DayStartUtc;

        var closes = await db.PositionHistories.AsNoTracking()
            .Where(trade => trade.ExchangeAccountId == scope.Account.Id
                && trade.CloseTime >= dayStart && trade.CloseTime <= now)
            .OrderBy(trade => trade.CloseTime)
            .ThenBy(trade => trade.Id)
            .ToListAsync(ct);

        // 与旧执行链路同口径：当前指纹下、最近一次非 hold 之后的连续 hold 次数。
        var recentActions = await MemoryQuery()
            .OrderByDescending(proposal => proposal.CreatedAt)
            .ThenByDescending(proposal => proposal.Id)
            .Take(100)
            .Select(proposal => proposal.Action)
            .ToListAsync(ct);
        var holds = 0;
        foreach (var action in recentActions)
        {
            if (action != "hold") break;
            holds++;
        }

        return TradingRiskPolicy.EvaluateDiscipline(closes, now, zone, holds);
    }

    public async Task<IReadOnlyList<TradingCycle>> GetCyclesAsync(DateTime sinceUtc, int limit, CancellationToken ct)
    {
        var since = AsUtc(sinceUtc);
        return await db.TradingCycles.AsNoTracking()
            .Where(cycle => cycle.TraderId == scope.TraderId && cycle.StrategyId == scope.StrategyId
                && cycle.ExchangeAccountId == scope.Account.Id && cycle.StartedAt >= since)
            .OrderByDescending(cycle => cycle.StartedAt)
            .ThenByDescending(cycle => cycle.Id)
            .Take(Math.Clamp(limit, 1, CycleLimit))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TraderLesson>> GetLessonsAsync(int limit, CancellationToken ct) =>
        await db.TraderLessons.AsNoTracking()
            .Where(lesson => lesson.TraderId == scope.TraderId)
            .OrderByDescending(lesson => lesson.CreatedAt)
            .ThenByDescending(lesson => lesson.Id)
            .Take(Math.Clamp(limit, 1, LessonLimit))
            .ToListAsync(ct);

    /// <summary>
    /// 提案记忆基础查询：绑定交易员 + 策略 + 精确指纹，并通过周期关联；
    /// 排除错误占位提案与失败/中止周期的提案（LC-STD-04 §5.1「失败阶段的提案」）。
    /// </summary>
    private IQueryable<TradeProposal> MemoryQuery() =>
        from proposal in db.TradeProposals.AsNoTracking()
        join cycle in db.TradingCycles.AsNoTracking() on proposal.CycleId equals cycle.Id
        where proposal.TraderId == scope.TraderId
            && (proposal.Action == "open" || proposal.Action == "close" || proposal.Action == "hold")
            && cycle.TraderId == scope.TraderId
            && cycle.StrategyId == scope.StrategyId
            && cycle.MemoryFingerprint == scope.MemoryFingerprint
            && cycle.Status != "failed"
            && cycle.Status != "aborted"
        select proposal;

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
