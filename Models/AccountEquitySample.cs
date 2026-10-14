namespace LuckyCat.Models;

// 账户净值采样：由 5 秒同步器周期采集（约每 60 秒一个点；与最近样本无变化且较新时跳过以控制体积）。
// 净值口径 = 钱包余额 + 未实现盈亏（保证金余额语义，与看板展示一致）。
public class AccountEquitySample
{
    public int Id { get; set; }
    public int ExchangeAccountId { get; set; }
    public DateTime At { get; set; }
    public decimal Equity { get; set; }
}
