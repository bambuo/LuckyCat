using LuckyCat.Contracts;

namespace LuckyCat.Workflows;

// 周期事件的 SSE 负载契约（LC-STD-03 §4.2）：事件的权威落库与推送由周期宿主（Task 11）
// 在其 ICycleRecorder 实现里完成——RecordStageAsync 发布 stage、提案/裁决发布 proposal/verdict、
// CompleteCycleAsync 发布 cycle，全部发到频道单点决策 TradingCycleContext.SseChannel
// （R-11-7：试运行按周期独立 test-run 频道，其余按 SseChannels.Trader(traderId)）。
// 负载字段名与 §4.2 逐字一致（宿主与前端按同一契约消费）；金额、数量、百分比、杠杆一律字符串
// （LC-STD-00 语义规则 4），时间一律 UTC。order / tool / equity / position-history / state / ping
// 由各自的既有发布者（写工具、净值采样、端点）负责，不在本文件重复定义。

/// <summary>cycle 事件：周期开始（宿主在图启动前发布，status=running）。</summary>
public sealed record CycleStartedEvent(string CycleId, int? TraderId, string Mode, string Trigger,
    string Status, DateTime StartedAt, DateTime? CompletedAt, string? Error);

/// <summary>cycle 事件：周期终态。Status 与 CycleOutcome 一致；errors 非空时 ErrorCode/Error 说明原因。</summary>
public sealed record CycleCompletedEvent(string CycleId, int? TraderId, string Mode, string Trigger,
    string Status, DateTime StartedAt, DateTime? CompletedAt, string? Error);

/// <summary>
/// stage 事件：一个角色阶段完成或失败（无 AgentStageRun 的收尾阶段不发布本事件）。
/// StageId 与 AgentStageRun.Id、ToolInvocation.stageRunId 同源。
/// </summary>
public sealed record StageRecordedEvent(string CycleId, string StageId, string Role, string Status,
    int Attempt, long ElapsedMs, int? InputTokens, int? OutputTokens, int ToolCalls, string? Summary,
    string? Error);

/// <summary>
/// verdict 事件：风控裁决生成（仅 open 路径；负载即 RiskVerdict 对象，数值为字符串）。
/// 提案事件直接复用 <see cref="TradeProposalDto"/>，不在此重复定义。
/// </summary>
public sealed record VerdictRecordedEvent(string Id, string CycleId, string ProposalId, string Decision,
    IReadOnlyList<string> Reasons, string? MaxPositionPct, string? MaxLeverage, string? EntryZoneMin,
    string? EntryZoneMax, bool? AllowMarket, DateTime? ValidUntilUtc);
