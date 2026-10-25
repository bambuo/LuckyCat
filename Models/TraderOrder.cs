namespace LuckyCat.Models;

// 交易员订单审计：每一笔真实下单/平仓/演练跳过都留痕（实盘执行可追溯）
public class TraderOrder
{
    public int Id { get; set; }
    public int TraderId { get; set; }
    public DateTime At { get; set; }
    public string? Symbol { get; set; }
    /// <summary>动作：buy | sell | close | skip</summary>
    public required string Action { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    /// <summary>开仓时附带的止损价（供持仓段注入「距离止损多远」的判断；仅开仓行有值）</summary>
    public decimal? StopLossPrice { get; set; }
    /// <summary>开仓时附带的止盈价（同上；可空）</summary>
    public decimal? TakeProfitPrice { get; set; }
    /// <summary>订单生命周期状态：submitted | filled | partial | failed | unknown | cancelled（P7 删除 Success 后以此为准）</summary>
    public string? Status { get; set; }
    /// <summary>关联周期；过渡期旧代码可写 null</summary>
    public string? CycleId { get; set; }
    /// <summary>交易所账户快照；合成账户试运行为 null</summary>
    public int? ExchangeAccountId { get; set; }
    public string? ProposalId { get; set; }
    public string? VerdictId { get; set; }
    /// <summary>幂等键；与 CycleId 组成唯一索引</summary>
    public string? ClientOrderId { get; set; }
    /// <summary>持仓方向：long | short（平仓时为被平仓方向）</summary>
    public string? Side { get; set; }
    public decimal? FilledQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
    /// <summary>实际提交杠杆</summary>
    public decimal? Leverage { get; set; }
    /// <summary>是否演练（演练成交为 filled + simulated=true）</summary>
    public bool Simulated { get; set; }
    public string? ExchangeOrderId { get; set; }
    public bool Success { get; set; }
    public string? Note { get; set; }
    public string? Error { get; set; }
}
