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
    string? Quantity,
    string? Price,
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
    bool IsRunning,
    string ExecutionMode,
    bool IsCrossMargin,
    int ScanIntervalMinutes,
    string? InitialBalance,
    DateTime? StartedAt,
    DateTime? LastCycleAt,
    string? LastCycleStatus,
    bool CapabilityCanTrade,
    IReadOnlyList<string> CapabilityNotes,
    string? PositionMode);

public record DashboardAccount(
    bool Success,
    string? Error,
    string? Equity,
    string? Available,
    string? TotalPnl,
    string? PnlPct,
    int PositionCount,
    string? MarginUsedPct,
    DateTime At);

public record DashboardSample(DateTime At, string Equity);

public record DashboardPosition(
    string Symbol,
    string Side,
    string Quantity,
    string AverageOpenPrice,
    string? UnrealizedPnl,
    string? LiquidationPrice,
    string? Leverage);

public record DashboardMetrics(
    int TotalTrades,
    string WinRate,
    string TotalPnl,
    string TotalFee,
    string NetPnl,
    string ProfitFactor,
    string AvgWin,
    string AvgLoss,
    string MaxDrawdownPct,
    string Sharpe);

public record DashboardDirectionStat(string Side, int Count, string WinRate, string TotalPnl, string AvgPnl);

public record DashboardSymbolStat(string Symbol, int Count, string WinRate, string TotalPnl);

public record DashboardHistoryRow(
    string Symbol,
    string? Side,
    string? EntryPrice,
    string? ExitPrice,
    string? Quantity,
    string? Leverage,
    string RealizedPnl,
    string? Fee,
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
    DashboardHistory History,
    IReadOnlyList<TraderOrderDto> RecentOrders,
    // 新链路最新周期块（Task 14 B：周期/阶段/提案/裁决/订单；无周期时为 null）。
    CycleDetailDto? LatestCycle = null);
