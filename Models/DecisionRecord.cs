namespace LuckyCat.Models;

// AI 调用轮次记录（成功决策和失败轮次都保存）；「上下文增强 · 历史决策记忆」的数据来源。
// 「历史决策记忆」开关仅控制注入：关闭时本轮不注入，但记录仍持续积累。
public class DecisionRecord
{
    public int Id { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? ExecutionSummary { get; set; }
    public int StrategyId { get; set; }
    /// <summary>来源交易员（交易员后台执行时写入；手工 AI 测试为 null）</summary>
    public int? TraderId { get; set; }
    public int ProviderId { get; set; }
    public required string ProviderName { get; set; }
    public required string Model { get; set; }
    public string? Symbol { get; set; }
    public string? Action { get; set; }
    public double? Confidence { get; set; }
    public required string DecisionJson { get; set; }
    /// <summary>思维链（模型 reasoning，截断 2000 字符）</summary>
    public string? Reasoning { get; set; }
    /// <summary>本轮系统提示词原文（完整落库，供看板「提示词」页展示；历史记录可为 null）</summary>
    public string? SystemPrompt { get; set; }
    /// <summary>本轮用户提示词原文（完整落库）</summary>
    public string? UserPrompt { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public long? ElapsedMs { get; set; }
    public DateTime CreatedAt { get; set; }
}
