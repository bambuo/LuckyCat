namespace LuckyCat.Workflows;

// 周期事件的 SSE 负载契约（LC-STD-03 §5）：事件的权威落库与推送由周期宿主（Task 11）
// 在其 ICycleRecorder 实现里完成——RecordStageAsync 发布 stage 事件、
// CompleteCycleAsync 发布 cycle 事件到 SseChannels.Trader(traderId)。
// 本文件只定义负载形状，保证宿主、复盘与前端按同一契约消费。

/// <summary>cycle 事件：周期开始（宿主在图启动前发布）。</summary>
public sealed record CycleStartedEvent(string CycleId, int TraderId, string Mode, string Trigger,
    DateTimeOffset StartedAtUtc);

/// <summary>stage 事件：一个角色阶段已记录（无 AgentStageRun 的收尾阶段不发布本事件）。</summary>
public sealed record StageRecordedEvent(string CycleId, string Role, DateTimeOffset AtUtc);

/// <summary>cycle 事件：周期终态。Status 与 CycleOutcome 一致；errors 非空时 ErrorCode/Error 说明原因。</summary>
public sealed record CycleCompletedEvent(string CycleId, string Status, string? ErrorCode, string? Error,
    DateTimeOffset CompletedAtUtc);
