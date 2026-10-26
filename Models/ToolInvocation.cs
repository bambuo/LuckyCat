namespace LuckyCat.Models;

// 工具调用明细：智能体阶段内的每一次工具执行（入参脱敏、回执可截断）
public class ToolInvocation
{
    public long Id { get; set; }
    /// <summary>交易周期；复盘工具调用为 null</summary>
    public string? CycleId { get; set; }
    public required string StageRunId { get; set; }
    /// <summary>工具名（snake_case）</summary>
    public required string ToolName { get; set; }
    /// <summary>入参 JSON，已脱敏</summary>
    public required string ArgumentsJson { get; set; }
    public required string ResultJson { get; set; }
    /// <summary>业务成功（区别于传输成功）</summary>
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public long ElapsedMs { get; set; }
    /// <summary>结果是否被截断</summary>
    public bool Truncated { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
