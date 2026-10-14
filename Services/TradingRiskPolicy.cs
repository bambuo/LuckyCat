using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Models;

namespace LuckyCat.Services;

public sealed record TradingDiscipline(DateTime DayStartUtc, DateTime ResetAtUtc, int ConsecutiveLosses,
    bool StopOpening, bool FeesKnown, int ConsecutiveHolds = 0)
{
    public decimal PositionMultiplier => ConsecutiveLosses >= 2 ? 0.5m : 1m;
}

public sealed record OpenRiskContext(decimal Capital, decimal Available, decimal UsedMargin, int PositionCount,
    bool AlreadyHeld, decimal ExecutionPrice, decimal ExchangeMaxLeverage, decimal QuantityStep,
    decimal MinQuantity, decimal ExchangeMinNotional, TradingDiscipline Discipline);

public sealed record OpenTradePlan(decimal Price, decimal Quantity, decimal Leverage, decimal Margin,
    decimal RiskAmount, decimal NetRiskRewardRatio, decimal StopLossPrice, decimal TakeProfitPrice);

/// <summary>无外部副作用的交易纪律、输出契约与资金风控。执行器必须在下单前调用。</summary>
public static class TradingRiskPolicy
{
    public static decimal? NetRiskReward(decimal entry, decimal stopDistance, decimal target, bool isLong, RiskControlConfig r)
    {
        if (entry <= 0 || stopDistance <= 0) return null;
        var cost = entry * (r.EstimatedFeePct + r.EstimatedSlippagePct + r.EstimatedFundingPct) / 100m;
        var reward = isLong ? target - entry : entry - target;
        return (reward - cost) / (stopDistance + cost);
    }
    public static string? ValidateConfig(RiskControlConfig r)
    {
        if (r.MaxPositions is < 1 or > 100 || r.BtcEthLeverage is < 1 or > 125 || r.AltcoinLeverage is < 1 or > 125)
            return "持仓数量或杠杆配置超出范围";
        if (r.BtcEthPositionPct is <= 0 or > 100 || r.AltcoinPositionPct is <= 0 or > 100 ||
            r.MaxMarginUsage is <= 0 or > 100 || r.MaxRiskPerTradePct is <= 0 or > 100)
            return "仓位、保证金使用率与单笔风险必须在 (0,100] 内";
        if (r.MinConfidence is < 0 or > 100 || r.MinRiskRewardRatio is <= 0 or > 100 || r.MinPositionSize <= 0)
            return "信心度、盈亏比或最小开仓金额配置无效";
        if (r.EstimatedFeePct is < 0 or > 10 || r.EstimatedSlippagePct is < 0 or > 10 ||
            r.EstimatedFundingPct is < 0 or > 10 || r.MaxEntryDeviationPct is <= 0 or > 10)
            return "成本预算或最大执行价偏离配置无效";
        return null;
    }

    public static TradingDiscipline EvaluateDiscipline(IEnumerable<PositionHistory> closes, DateTime nowUtc,
        TimeZoneInfo zone, int consecutiveHolds = 0)
    {
        var day = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        var start = TimeZoneInfo.ConvertTimeToUtc(day, zone);
        var reset = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1), zone);
        var losses = 0;
        var stopped = false;
        var feesKnown = true;
        foreach (var close in closes.Where(c => c.CloseTime >= start && c.CloseTime <= nowUtc).OrderBy(c => c.CloseTime).ThenBy(c => c.Id))
        {
            if (close.Fee is null) feesKnown = false;
            losses = close.RealizedPnl - (close.Fee ?? 0m) < 0 ? losses + 1 : 0;
            stopped |= losses >= 3; // 当日停手一旦触发，即使其他交易产生盈利也不会解锁。
        }
        return new(start, reset, losses, stopped, feesKnown, consecutiveHolds);
    }

    public static string? ValidateDecision(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return "decision 必须是对象";
        var action = String(d, "action");
        if (action is not ("buy" or "sell" or "hold" or "close")) return "action 无效";
        var confidence = Number(d, "confidence");
        if (confidence is null || confidence is < 0 or > 100 || confidence != decimal.Truncate(confidence.Value))
            return "confidence 必须是 0–100 整数";
        if (string.IsNullOrWhiteSpace(String(d, "reason"))) return "reason 必填";
        if (action != "hold" && !IsSymbol(String(d, "symbol"))) return "symbol 必须是 BASE/USDT";
        if (action == "hold" && String(d, "symbol") is { } heldSymbol && !IsSymbol(heldSymbol)) return "hold 的 symbol 格式无效";
        if (action == "hold" && string.IsNullOrWhiteSpace(String(d, "trigger"))) return "hold 的 trigger 必填（无法评估时说明待恢复的数据）";
        if (action != "hold" && d.TryGetProperty("trigger", out var trigger) && trigger.ValueKind != JsonValueKind.Null)
            return "开平仓的 trigger 必须为 null；未触发条件应输出 hold";
        if (action is "hold" or "close")
            foreach (var key in new[] { "entry_price", "stop_loss_pct", "take_profit_pct", "position_pct", "leverage" })
                if (d.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null) return $"{action} 的 {key} 必须为 null";
        if (action is "buy" or "sell")
        {
            if (Number(d, "entry_price") is not > 0 || Number(d, "stop_loss_pct") is not (> 0 and < 100) ||
                Number(d, "take_profit_pct") is not (> 0 and < 100) || Number(d, "position_pct") is not (> 0 and <= 100) ||
                Number(d, "leverage") is not >= 1) return "开仓必填数值缺失或超出范围";
        }
        return null;
    }

    public static bool IsSymbol(string? value) => value is not null && value.EndsWith("/USDT", StringComparison.Ordinal) &&
        value.Length > 5 && value[..^5].All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));

    public static string BaseAsset(string symbol) => symbol.Contains('/') ? symbol.Split('/')[0].ToUpperInvariant() :
        symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? symbol[..^4].ToUpperInvariant() : symbol.ToUpperInvariant();

    public static string? ValidateOpen(JsonElement decision, RiskControlConfig r, OpenRiskContext c, out OpenTradePlan? plan)
    {
        plan = null;
        var invalid = ValidateConfig(r) ?? ValidateDecision(decision);
        if (invalid is not null) return invalid;
        if (String(decision, "action") is not ("buy" or "sell")) return "仅支持开仓决策";
        if (c.Capital <= 0 || c.Available < 0 || c.UsedMargin < 0 || c.ExecutionPrice <= 0 || c.ExchangeMaxLeverage < 1)
            return "账户或执行行情不可用";
        if (c.Discipline.StopOpening) return "当日已触发连续三笔亏损停手";
        if (!c.Discipline.FeesKnown) return "当日平仓手续费缺失，无法确认净盈亏纪律";
        if (c.AlreadyHeld) return "已有该币种持仓，不加仓";
        if (c.PositionCount >= r.MaxPositions) return "已达到最大持仓数量";
        var symbol = String(decision, "symbol")!;
        var major = BaseAsset(symbol) is "BTC" or "ETH";
        var leverage = Number(decision, "leverage")!.Value;
        if (leverage > Math.Min(major ? r.BtcEthLeverage : r.AltcoinLeverage, c.ExchangeMaxLeverage)) return "杠杆超过策略或交易所上限";
        var pct = Number(decision, "position_pct")!.Value;
        var cap = (major ? r.BtcEthPositionPct : r.AltcoinPositionPct) * c.Discipline.PositionMultiplier;
        if (pct > cap) return "保证金比例超过策略上限（已计入连亏减半）";
        if (Number(decision, "confidence") < r.MinConfidence) return "信心度低于策略下限";
        var reference = Number(decision, "entry_price")!.Value;
        if (Math.Abs(c.ExecutionPrice / reference - 1m) * 100m > r.MaxEntryDeviationPct) return "执行价偏离参考价过大";
        var sl = Number(decision, "stop_loss_pct")!.Value;
        var tp = Number(decision, "take_profit_pct")!.Value;
        var cost = r.EstimatedFeePct + r.EstimatedSlippagePct + r.EstimatedFundingPct;
        var netRatio = NetRiskReward(c.ExecutionPrice, c.ExecutionPrice * sl / 100m,
            c.ExecutionPrice * (1m + (String(decision, "action") == "buy" ? tp : -tp) / 100m),
            String(decision, "action") == "buy", r)!.Value;
        if (netRatio < r.MinRiskRewardRatio) return "扣除成本预算后的盈亏比不足";
        var notional = c.Capital * pct / 100m * leverage;
        var quantity = notional / c.ExecutionPrice;
        if (c.QuantityStep > 0) quantity = Math.Floor(quantity / c.QuantityStep) * c.QuantityStep;
        notional = quantity * c.ExecutionPrice;
        if (quantity <= 0 || quantity < c.MinQuantity) return "数量取整后低于最小交易量";
        if (notional < Math.Max(r.MinPositionSize, c.ExchangeMinNotional)) return "数量取整后低于策略或交易所最小开仓金额";
        var margin = notional / leverage;
        var loss = notional * (sl + cost) / 100m;
        if (loss > c.Capital * r.MaxRiskPerTradePct / 100m) return "含成本预算的单笔风险超过上限";
        if (margin + notional * cost / 100m > c.Available) return "可用保证金不足（含成本预留）";
        if (c.UsedMargin + margin > c.Capital * r.MaxMarginUsage / 100m) return "开仓后保证金使用率超过上限";
        var isLong = String(decision, "action") == "buy";
        plan = new(c.ExecutionPrice, quantity, leverage, margin, loss, netRatio,
            c.ExecutionPrice * (1m + (isLong ? -sl : sl) / 100m),
            c.ExecutionPrice * (1m + (isLong ? tp : -tp) / 100m));
        return null;
    }

    private static string? String(JsonElement d, string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? Number(JsonElement d, string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;
}
