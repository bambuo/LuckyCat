namespace LuckyCat.Models;

// 智能体阶段运行：每轮周期内各角色（分析/交易/风控/执行/复盘）的一次调用审计
public class AgentStageRun
{
    public required string Id { get; set; }
    /// <summary>交易周期；复盘任务为 null</summary>
    public string? CycleId { get; set; }
    /// <summary>试运行为 null；复盘任务填写所属交易员</summary>
    public int? TraderId { get; set; }
    /// <summary>角色：analyst | trader | risk | executor | reviewer</summary>
    public required string Role { get; set; }
    /// <summary>尝试次数，从 1 起</summary>
    public int Attempt { get; set; } = 1;
    /// <summary>阶段状态：completed | failed | skipped</summary>
    public required string Status { get; set; }
    public required string ProviderName { get; set; }
    public required string Model { get; set; }
    /// <summary>系统指令全文</summary>
    public required string Instructions { get; set; }
    /// <summary>消息记录（含工具调用）</summary>
    public required string TranscriptJson { get; set; }
    public string? OutputJson { get; set; }
    public string? Reasoning { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public long ElapsedMs { get; set; }
    public int ToolCallCount { get; set; }
    public string? Error { get; set; }
    /// <summary>消息是否被截断</summary>
    public bool Truncated { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
