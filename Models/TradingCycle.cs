namespace LuckyCat.Models;

// 交易周期：一轮多智能体决策的完整审计单元（定时、手动或试运行触发）
public class TradingCycle
{
    public required string Id { get; set; }
    public int? TraderId { get; set; }
    public int StrategyId { get; set; }
    /// <summary>模型提供商快照；试运行也必填</summary>
    public int? ProviderId { get; set; }
    /// <summary>交易所账户快照；合成账户试运行为 null</summary>
    public int? ExchangeAccountId { get; set; }
    /// <summary>执行模式：dry | live</summary>
    public required string Mode { get; set; }
    /// <summary>触发来源：schedule | manual | test</summary>
    public required string Trigger { get; set; }
    /// <summary>状态：running | completed | failed | skipped | aborted</summary>
    public required string Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>稳定错误码，如 cancelled_by_user / process_restart / risk_stage_failed</summary>
    public string? ErrorCode { get; set; }
    /// <summary>终态错误摘要</summary>
    public string? Error { get; set; }
    public required string StrategyConfigJson { get; set; }
    public required string MemoryFingerprint { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public long? ElapsedMs { get; set; }
}
