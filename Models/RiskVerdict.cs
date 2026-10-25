namespace LuckyCat.Models;

// 风控裁决：风控智能体对开仓提案的批准/拒绝结论（一提案一裁决）
public class RiskVerdict
{
    public required string Id { get; set; }
    public required string CycleId { get; set; }
    public required string ProposalId { get; set; }
    /// <summary>裁决：approved | rejected</summary>
    public required string Decision { get; set; }
    /// <summary>理由 JSON 数组</summary>
    public required string ReasonsJson { get; set; }
    /// <summary>收紧后的仓位上限（百分点）</summary>
    public decimal? MaxPositionPct { get; set; }
    /// <summary>收紧后的杠杆上限</summary>
    public decimal? MaxLeverage { get; set; }
    public decimal? EntryZoneMin { get; set; }
    public decimal? EntryZoneMax { get; set; }
    /// <summary>是否允许市价</summary>
    public bool? AllowMarket { get; set; }
    public DateTime? ValidUntilUtc { get; set; }
    /// <summary>模型原始输出</summary>
    public required string RawJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
