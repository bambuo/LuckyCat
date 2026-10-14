using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>默认策略逐币条件参考。只计算已收盘行情，OI/新闻不参与布尔门槛。</summary>
public static class SignalEvaluator
{
    public static string? DirectionTimeframe(StrategyConfig config)
    {
        var k = config.Indicators.Klines;
        return k.EnableMultiTimeframe ? k.SelectedTimeframes
            .Where(t => IndicatorCalculator.TryParseInterval(t) > IndicatorCalculator.TryParseInterval(k.PrimaryTimeframe))
            .OrderBy(t => IndicatorCalculator.TryParseInterval(t)).FirstOrDefault() : null;
    }

    public static SignalAssessment Evaluate(string symbol, StrategyConfig config,
        MarketSnapshot? primary, MarketSnapshot? direction, DateTime nowUtc)
    {
        var settings = config.Indicators;
        var directionFrame = DirectionTimeframe(config);
        var p = primary?.Source == "live" ? IndicatorCalculator.CalculateSignalValues(settings, primary.Klines, settings.Klines.PrimaryTimeframe, primary.AsOfUtc ?? nowUtc) : null;
        var d = direction?.Source == "live" && directionFrame is not null
            ? IndicatorCalculator.CalculateSignalValues(settings, direction.Klines, directionFrame, direction.AsOfUtc ?? nowUtc) : null;
        var price = primary is { TickerAvailable: true, LastPrice: > 0 } ? primary.LastPrice : null;
        return EvaluateValues(symbol, config, p, d, price, directionFrame);
    }

    internal static SignalAssessment EvaluateValues(string symbol, StrategyConfig config,
        IndicatorCalculator.SignalValues? p, IndicatorCalculator.SignalValues? d, decimal? price, string? directionFrame)
    {
        var settings = config.Indicators;
        return new(symbol, directionFrame, PromptComposer.UsesDefaultEntry(config), Side(true), Side(false));

        SignalSideAssessment Side(bool isLong)
        {
            bool? trend = d is { EmaFast: { } fast, EmaSlow: { } slow, Bars.Count: > 0 }
                ? isLong ? d.Bars[^1].Close > fast && fast > slow : d.Bars[^1].Close < fast && fast < slow : null;
            bool? momentum = p?.Histogram is { } hist && (!settings.Rsi.Enabled || p.Rsi is not null)
                ? (isLong ? hist > 0 : hist < 0) && (!settings.Rsi.Enabled || (isLong ? p.Rsi > 50 : p.Rsi < 50)) : null;
            bool? position = null;
            if (p is { EmaFast: { } ema, Atr: > 0, Bars.Count: >= 4 })
            {
                var atr = p.Atr.Value;
                var last = p.Bars[^1];
                // 区间相交定义触及；收盘必须回到均线相应一侧。
                var pullback = last.High >= ema - 0.5m * atr && last.Low <= ema + 0.5m * atr &&
                    (isLong ? last.Close >= ema : last.Close <= ema);
                var boundary = isLong ? p.Bars.SkipLast(1).TakeLast(2).Max(b => b.High) : p.Bars.SkipLast(1).TakeLast(2).Min(b => b.Low);
                var distance = isLong ? last.Close - boundary : boundary - last.Close;
                var previousBoundary = isLong ? p.Bars.SkipLast(2).TakeLast(2).Max(b => b.High) : p.Bars.SkipLast(2).TakeLast(2).Min(b => b.Low);
                var previousDistance = isLong ? p.Bars[^2].Close - previousBoundary : previousBoundary - p.Bars[^2].Close;
                var weakConfirmed = previousDistance > 0 && previousDistance < 0.25m * atr &&
                    (isLong ? last.Close > previousBoundary : last.Close < previousBoundary);
                var sharp = isLong ? last.Close - p.Bars[^3].Open > 3m * atr : p.Bars[^3].Open - last.Close > 3m * atr;
                position = pullback || (!sharp && (distance >= 0.25m * atr || weakConfirmed));
            }
            var stop = p?.Atr is > 0 ? p.Atr * 1.5m : null;
            var target = price is { } entry ? NearestTarget(p?.Bars ?? [], d?.Bars ?? [], entry, isLong) : null;
            var ratio = price is { } e && stop is { } sl && target is { } t
                ? TradingRiskPolicy.NetRiskReward(e, sl, t, isLong, config.RiskControl) : null;
            bool? reward = ratio is { } rr ? rr >= config.RiskControl.MinRiskRewardRatio : null;
            var failures = new List<string>();
            foreach (var (name, value) in new[] { ("方向", trend), ("动能", momentum), ("位置", position), ("成本后盈亏比", reward) })
                if (value != true) failures.Add($"{name}{(value is null ? "未知" : "未满足")}");
            return new(trend, momentum, position, reward, price, stop, target, ratio, failures);
        }
    }

    // 主周期与方向周期最近20根已收盘K线的已确认三根摆动点，仅取当前参考价方向上的最近点。
    internal static decimal? NearestTarget(IReadOnlyList<MarketKline> primary, IReadOnlyList<MarketKline> direction,
        decimal price, bool isLong)
    {
        var targets = new List<decimal>();
        foreach (var series in new[] { primary, direction })
        {
            var bars = series.TakeLast(20).ToList();
            for (var i = 1; i < bars.Count - 1; i++)
            {
                if (isLong && bars[i].High > bars[i - 1].High && bars[i].High >= bars[i + 1].High && bars[i].High > price) targets.Add(bars[i].High);
                if (!isLong && bars[i].Low < bars[i - 1].Low && bars[i].Low <= bars[i + 1].Low && bars[i].Low < price) targets.Add(bars[i].Low);
            }
        }
        return targets.Count == 0 ? null : isLong ? targets.Min() : targets.Max();
    }
}
