namespace LuckyCat.Contracts;

// API DTO：金额、数量、百分比、杠杆一律字符串传输（LC-STD-03）。
public sealed class TradeProposalDto
{
    public required string Id { get; set; }
    public required string Action { get; set; }
    public string? Symbol { get; set; }
    public string? Side { get; set; }
    public string? Confidence { get; set; }
    public string? EntryPrice { get; set; }
    public string? StopLossPrice { get; set; }
    public string? TakeProfitPrice { get; set; }
    public string? PositionPct { get; set; }
    public string? Leverage { get; set; }
    public required string Thesis { get; set; }
    public string? Invalidation { get; set; }
}

// 手动平仓请求（Task 14 端点使用，Task 11 CloseManuallyAsync 消费）。
public sealed record ManualCloseRequest(string Symbol, string? Quantity, string OrderType, string? Price, string Reason);

// —— Task 14 B 组：周期/看板/复盘/SSE 契约 DTO（LC-STD-03 §1.3/§3.4/§3.5/§4.2/§5.1）——
// 金额、数量、百分比、杠杆一律字符串；时间一律 UTC。null=未知/未提供、[]=有数据集但为空（§1.1）。

/// <summary>分页列表载荷（§1.3）：page 从 1 起；空列表 items 为 []、total 为 0，不得返回 null。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>周期摘要（§5.1 cycle 块）：strategyConfigJson 与 memoryFingerprint 仅存储供审计，不返回（§2.1）。</summary>
public sealed record CycleInfoDto(
    string Id,
    int? TraderId,
    int StrategyId,
    int? ProviderId,
    int? ExchangeAccountId,
    string Mode,
    string Trigger,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? ErrorCode,
    string? Error,
    int? InputTokens,
    int? OutputTokens,
    long? ElapsedMs);

/// <summary>周期详情 stages[] 单项（§5.1）：summary 由阶段输出快照推导，不新增落库列。</summary>
public sealed record CycleStageDto(
    string Id,
    string Role,
    string Status,
    long ElapsedMs,
    int? InputTokens,
    int? OutputTokens,
    int ToolCalls,
    string? Summary,
    string? Error);

/// <summary>周期详情 verdict（§5.1）：金额字符串；仅 open 提案路径返回（API-08）。</summary>
public sealed record CycleVerdictDto(
    string Id,
    string Decision,
    IReadOnlyList<string> Reasons,
    string? MaxPositionPct,
    string? MaxLeverage,
    string? EntryZoneMin,
    string? EntryZoneMax,
    bool? AllowMarket,
    DateTime? ValidUntilUtc);

/// <summary>周期详情/SSE order 载荷（§4.2 order / §5.1 orders）：数量与价格一律字符串。</summary>
public sealed record CycleOrderDto(
    int OrderId,
    string? CycleId,
    int? TraderId,
    int? ExchangeAccountId,
    string? ProposalId,
    string Action,
    string? Side,
    string? Symbol,
    string? Quantity,
    string? FilledQuantity,
    string? Price,
    string? AveragePrice,
    string? Status,
    bool Simulated,
    string? Note,
    DateTime At);

/// <summary>GET /api/cycles/{cycleId} 载荷（§3.5/§5.1）。</summary>
public sealed record CycleDetailDto(
    CycleInfoDto Cycle,
    IReadOnlyList<CycleStageDto> Stages,
    TradeProposalDto? Proposal,
    CycleVerdictDto? Verdict,
    IReadOnlyList<CycleOrderDto> Orders);

/// <summary>阶段全文的单次工具调用摘要（§3.5「工具调用摘要」）：不含入参/回执全文（§4.2 同口径，详情只到审计行级）。</summary>
public sealed record StageToolCallDto(
    string ToolName,
    bool Success,
    string? ErrorCode,
    long ElapsedMs,
    bool Truncated,
    DateTime At);

/// <summary>GET /api/cycles/{cycleId}/stages/{stageRunId} 全文（§3.5）：transcript 为落库原文，Truncated 为截断标记。</summary>
public sealed record StageDetailDto(
    string Id,
    string? CycleId,
    int? TraderId,
    string Role,
    int Attempt,
    string Status,
    string ProviderName,
    string Model,
    string Instructions,
    string TranscriptJson,
    string? OutputJson,
    string? Reasoning,
    int? InputTokens,
    int? OutputTokens,
    long ElapsedMs,
    int ToolCallCount,
    string? Error,
    bool Truncated,
    DateTime CreatedAt,
    IReadOnlyList<StageToolCallDto> ToolCalls);

/// <summary>
/// POST /traders/{id}/close-position 的 data（§3.4：{ cycleId, order }；写工具被拒时 order 为 null，
/// 并带出周期的 errorCode/error 供 UI 解释「为什么什么都没发生」）。
/// </summary>
public sealed record ManualCloseResultDto(string CycleId, CycleOrderDto? Order, string? ErrorCode, string? Error);

/// <summary>POST /traders/{id}/reviews/run 的 data：复盘结果原样上报（触发成功≠复盘成功，status 区分）。</summary>
public sealed record ReviewRunDto(
    string Status,
    string? ErrorCode,
    string? StageRunId,
    long ElapsedMs,
    DateTime? PeriodStart,
    DateTime? PeriodEnd);

/// <summary>GET /traders/{id}/reviews 列表项：持久 reviewer 阶段行摘要。</summary>
public sealed record ReviewListItemDto(
    string Id,
    string Status,
    int Attempt,
    string ProviderName,
    string Model,
    long ElapsedMs,
    int? InputTokens,
    int? OutputTokens,
    int ToolCalls,
    string? Error,
    DateTime CreatedAt);

/// <summary>GET /traders/{id}/lessons 列表项：经验正文按 JSON 数组解析后返回（§2.3）。</summary>
public sealed record LessonDto(
    long Id,
    int TraderId,
    string StageRunId,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Mistakes,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Evidence,
    DateTime CreatedAt);

/// <summary>
/// state SSE 事件载荷（§4.2）：连接建立与 start/stop 触发；
/// lastCycleStatus 只取约定枚举或 null（API-10）。
/// </summary>
public sealed record TraderStateEvent(bool IsRunning, string? LastCycleStatus, DateTime? LastCycleAt);

/// <summary>position-history SSE 事件载荷（§4.2）：盈亏为字符串。</summary>
public sealed record PositionHistoryEventDto(
    long PositionHistoryId,
    string Symbol,
    string RealizedPnl,
    DateTime CloseTime);
