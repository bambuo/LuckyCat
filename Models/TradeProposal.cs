namespace LuckyCat.Models;

// 交易提案：交易员智能体的结构化输出（开仓/平仓/观望）
public class TradeProposal
{
    public required string Id { get; set; }
    public required string CycleId { get; set; }
    /// <summary>试运行为 null</summary>
    public int? TraderId { get; set; }
    /// <summary>提案动作：open | close | hold</summary>
    public required string Action { get; set; }
    public string? Symbol { get; set; }
    /// <summary>持仓方向：long | short；开仓时必填</summary>
    public string? Side { get; set; }
    /// <summary>置信度 0–1</summary>
    public decimal? Confidence { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? StopLossPrice { get; set; }
    public decimal? TakeProfitPrice { get; set; }
    /// <summary>仓位占比（保证金占权益的百分点）</summary>
    public decimal? PositionPct { get; set; }
    public decimal? Leverage { get; set; }
    /// <summary>理由</summary>
    public required string Thesis { get; set; }
    /// <summary>失效条件</summary>
    public string? Invalidation { get; set; }
    /// <summary>自检项 JSON 数组</summary>
    public string? ReviewChecklistJson { get; set; }
    /// <summary>模型原始输出</summary>
    public required string RawJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
