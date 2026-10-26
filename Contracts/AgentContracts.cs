namespace LuckyCat.Contracts;

// 智能体结构化输出契约：模型按 AgentJson.Options 反序列化；
// 与 Models.TradeProposal / Models.RiskVerdict 实体同名但职责不同，引用时使用命名空间全名或别名。
// 零值取安全分支（Hold/Rejected），缺失字段不得 fail-open。
public enum ProposalAction { Hold = 0, Open = 1, Close = 2 }
public enum TradeSide { Long, Short }
public enum RiskDecision { Rejected = 0, Approved = 1 }

public sealed record ResearchBrief(
    string MarketContext,
    List<string> Candidates,
    List<SymbolResearch> Symbols,
    List<string> DataWarnings);

public sealed record SymbolResearch(
    string Symbol, string Trend, List<string> KeyLevels,
    string IndicatorSummary, List<string> Risks);

public sealed record AgentTradeProposal(
    ProposalAction Action,
    string? Symbol,
    TradeSide? Side,
    decimal? Confidence,
    decimal? EntryPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    decimal? PositionPct,
    decimal? Leverage,
    string Thesis,
    string? Invalidation,
    List<string>? ReviewChecklist);

public sealed record AgentRiskVerdict(
    RiskDecision Decision,
    List<string> Reasons,
    decimal? MaxPositionPct,
    decimal? MaxLeverage,
    decimal? EntryZoneMin,
    decimal? EntryZoneMax,
    bool? AllowMarket,
    DateTime? ValidUntilUtc);

public sealed record ExecutionAttempt(string Tool, string Arguments, string Result, string? ErrorCode);
public sealed record ExecutionOrder(long OrderId, string ClientOrderId, string Status, decimal? FilledQuantity, decimal? AveragePrice);
public sealed record ExecutionReport(string Summary, List<ExecutionAttempt> Attempts, List<ExecutionOrder> Orders, List<string> Errors);

public sealed record ReviewReport(
    string Summary, List<string> Strengths, List<string> Mistakes,
    List<string> Rules, List<string> Evidence);
