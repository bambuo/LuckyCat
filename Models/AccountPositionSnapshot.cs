namespace LuckyCat.Models;

// 持仓采样：交易员每轮运行 / 打开看板 / 手动平仓前采样一次当前持仓（含真实杠杆），
// 供历史仓位重建时回填「杠杆」——Binance 等交易所对已平仓位不提供杠杆字段，只能靠持仓期间采样。
public class AccountPositionSnapshot
{
    public int Id { get; set; }
    public int ExchangeAccountId { get; set; }
    public DateTime At { get; set; }
    public required string Symbol { get; set; }
    public string? Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? Leverage { get; set; }
}
