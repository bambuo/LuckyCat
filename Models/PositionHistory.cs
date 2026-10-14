namespace LuckyCat.Models;

// 自维护仓位历史（nofx traderposition 的等价物）：由后台同步器从交易所成交明细/共享接口增量同步落库，
// 看板改为读本表——不依赖交易所 API 的保留窗口，历史不再随窗口滚失。
// DedupeKey 为交易所侧事件的稳定指纹，保证重复同步不产生重复行。
public class PositionHistory
{
    public int Id { get; set; }
    public int ExchangeAccountId { get; set; }
    public required string DedupeKey { get; set; }
    /// <summary>数据来源：binance-trades（成交明细 FIFO 重建）| shared（共享接口持仓历史）</summary>
    public required string Source { get; set; }
    public required string Symbol { get; set; }
    public string? Side { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal? Fee { get; set; }
    /// <summary>同步时不可得，展示时经「持仓采样 → 当前杠杆设置」回填</summary>
    public decimal? Leverage { get; set; }
    public DateTime? OpenTime { get; set; }
    public DateTime CloseTime { get; set; }
    public DateTime SyncedAt { get; set; }
}
