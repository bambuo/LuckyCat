namespace LuckyCat.Services;

/// <summary>
/// 已用保证金估算结果。<see cref="Complete"/> 为 false 表示至少一个持仓缺杠杆或缺未实现盈亏，
/// 此时 <see cref="UsedMargin"/> 只是账户差额下界，调用方必须标记数据不完整（incomplete）。
/// </summary>
public readonly record struct MarginEstimate(decimal UsedMargin, bool Complete);

/// <summary>
/// 已用保证金估算：账户工具、提示词账户摘要与开仓风控共用同一实现。
/// 可用资金通常已扣除未实现盈亏，因此取「持仓初始保证金之和」与「权益 + 未实现 - 可用」的较大者，
/// 避免浮盈把已占用误算为 0；任一持仓缺杠杆或缺未实现盈亏时不能用整份权益兜底（会恒等于满仓），
/// 退回账户差额（钳制为非负）。
/// </summary>
public static class MarginMath
{
    public static decimal UsedMargin(AccountSnapshot account, IReadOnlyList<SymbolPosition> positions) =>
        Estimate(account, positions).UsedMargin;

    public static MarginEstimate Estimate(AccountSnapshot account, IReadOnlyList<SymbolPosition> positions)
    {
        var difference = Math.Max(0m,
            account.UsdtEquity + positions.Sum(position => position.UnrealizedPnl ?? 0m) - account.UsdtAvailable);
        var held = 0m;
        foreach (var position in positions)
        {
            if (position.Leverage is not > 0 || position.UnrealizedPnl is null)
            {
                return new MarginEstimate(difference, false);
            }
            held += Math.Abs(position.Quantity * position.AverageOpenPrice) / position.Leverage.Value;
        }
        return new MarginEstimate(Math.Max(held, difference), true);
    }
}
