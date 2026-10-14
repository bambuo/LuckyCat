using LuckyCat.Models;

namespace LuckyCat.Contracts;

public record TraderDto(
    int Id,
    string Name,
    int ProviderId,
    string ProviderName,
    string Model,
    int ExchangeAccountId,
    ExchangeType ExchangeType,
    string AccountName,
    int StrategyId,
    string StrategyName,
    bool IsRunning,
    string ExecutionMode,
    bool IsCrossMargin,
    int ScanIntervalMinutes,
    decimal? InitialBalance,
    DateTime? StartedAt,
    DateTime? LastCycleAt,
    string? LastCycleStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record TraderUpsertRequest(
    string Name,
    int ProviderId,
    int ExchangeAccountId,
    int StrategyId,
    bool IsCrossMargin,
    int ScanIntervalMinutes,
    decimal? InitialBalance,
    string? ExecutionMode = null);

public record TraderClosePositionRequest(string Symbol);

public record TraderOrderDto(
    int Id,
    DateTime At,
    string? Symbol,
    string Action,
    decimal? Quantity,
    decimal? Price,
    bool Success,
    string? Note,
    string? Error);

// —— 看板聚合 ——

public record DashboardTraderInfo(
    int Id,
    string Name,
    string ProviderName,
    string Model,
    string ExchangeLabel,
    string AccountName,
    string StrategyName,
    string StrategyType,
    bool IsRunning,
    string ExecutionMode,
    bool IsCrossMargin,
    int ScanIntervalMinutes,
    decimal? InitialBalance,
    DateTime? StartedAt,
    DateTime? LastCycleAt,
    string? LastCycleStatus,
    bool CapabilityCanTrade,
    IReadOnlyList<string> CapabilityNotes,
    string? PositionMode);

public record DashboardAccount(
    bool Success,
    string? Error,
    decimal? Equity,
    decimal? Available,
    decimal? TotalPnl,
    double? PnlPct,
    int PositionCount,
    decimal? MarginUsedPct,
    DateTime At);

public record DashboardSample(DateTime At, decimal Equity);

public record DashboardPosition(
    string Symbol,
    string Side,
    decimal Quantity,
    decimal AverageOpenPrice,
    decimal? UnrealizedPnl,
    decimal? LiquidationPrice,
    decimal? Leverage);

public record DashboardDecision(
    int Id,
    DateTime At,
    string? Action,
    string? Symbol,
    double? Confidence,
    decimal? EntryPrice,
    decimal? StopLossPct,
    decimal? TakeProfitPct,
    decimal? PositionPct,
    decimal? Leverage,
    string? Reason,
    string? Reasoning,
    int? InputTokens,
    int? OutputTokens,
    long? ElapsedMs,
    int? TraderId,
    string? SystemPrompt,
    string? UserPrompt,
    string Status = "ok",
    string? FailureStage = null,
    string? Error = null,
    string? RawOutput = null,
    string? PromptRevision = null,
    bool? RequestSent = null);

public record DashboardMetrics(
    int TotalTrades,
    double WinRate,
    decimal TotalPnl,
    decimal TotalFee,
    decimal NetPnl,
    decimal ProfitFactor,
    decimal AvgWin,
    decimal AvgLoss,
    double MaxDrawdownPct,
    double Sharpe);

public record DashboardDirectionStat(string Side, int Count, double WinRate, decimal TotalPnl, decimal AvgPnl);

public record DashboardSymbolStat(string Symbol, int Count, double WinRate, decimal TotalPnl);

public record DashboardHistoryRow(
    string Symbol,
    string? Side,
    decimal? EntryPrice,
    decimal? ExitPrice,
    decimal? Quantity,
    decimal? Leverage,
    decimal RealizedPnl,
    decimal? Fee,
    DateTime CloseTime);

public record DashboardHistory(
    bool Success,
    string? Error,
    DashboardMetrics Metrics,
    IReadOnlyList<DashboardDirectionStat> Directions,
    IReadOnlyList<DashboardSymbolStat> Symbols,
    IReadOnlyList<DashboardHistoryRow> Rows,
    string? Note);

public record TraderDashboardDto(
    DashboardTraderInfo Trader,
    DashboardAccount? Account,
    IReadOnlyList<DashboardSample> Samples,
    IReadOnlyList<DashboardPosition> Positions,
    IReadOnlyList<DashboardDecision> Decisions,
    DashboardHistory History,
    IReadOnlyList<TraderOrderDto> RecentOrders);
