namespace LuckyCat.Models;

// 交易员经验：复盘任务产出的阶段性总结与规则沉淀
public class TraderLesson
{
    public long Id { get; set; }
    public int TraderId { get; set; }
    public required string StageRunId { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public required string Summary { get; set; }
    public required string StrengthsJson { get; set; }
    public required string MistakesJson { get; set; }
    public required string RulesJson { get; set; }
    public required string EvidenceJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
