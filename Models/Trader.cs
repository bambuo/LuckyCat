namespace LuckyCat.Models;

// AI 交易员实例：绑定 提供商 + 交易所账户 + 策略；启动后后台按扫描间隔真实执行（实盘/演练）
public class Trader
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int ProviderId { get; set; }
    public int ExchangeAccountId { get; set; }
    public int StrategyId { get; set; }
    public bool IsRunning { get; set; }
    /// <summary>执行模式：live=实盘下单 | dry=演练（只跑 AI 决策与审计，不下单）</summary>
    public string ExecutionMode { get; set; } = "live";
    public bool IsCrossMargin { get; set; } = true;
    /// <summary>扫描间隔（分钟，3–60）</summary>
    public int ScanIntervalMinutes { get; set; } = 3;
    public decimal? InitialBalance { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastCycleAt { get; set; }
    /// <summary>最近一轮结果：ok | error | skipped</summary>
    public string? LastCycleStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
