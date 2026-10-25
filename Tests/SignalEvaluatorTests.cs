using LuckyCat.Contracts;
using LuckyCat.Services;
using Xunit;

namespace LuckyCat.Tests;

/// <summary>
/// 从旧 <c>PromptOptimizationTests</c> 迁出的存续用例：逐币条件、结构目标与
/// 默认/自定义入场判定都属于新链路（SignalEvaluator / TradingRiskPolicy / IndicatorCalculator）。
/// </summary>
public class SignalEvaluatorTests
{
    [Theory]
    [InlineData("ARB/USDT", 0.18835, 0.188035, 0.185603, -0.000186, 34.4)]
    [InlineData("PUMP/USDT", 0.005096, 0.0050747, 0.0050567, -0.0000003, 42)]
    [InlineData("ETH/USDT", 2531.69, 2520.61, 2499.35, -0.63, 39)]
    public void Round143DirectionIsLongForAllThreeButMomentumFails(string symbol, double close,
        double fast, double slow, double histogram, double rsi)
    {
        var bar = new MarketKline(DateTime.UtcNow.AddHours(-1), (decimal)close, (decimal)close, (decimal)close, (decimal)close, 100);
        var direction = new IndicatorCalculator.SignalValues([bar], (decimal)fast, (decimal)slow, 0, 60, 1);
        var primary = direction with { Histogram = (decimal)histogram, Rsi = (decimal)rsi };
        var result = SignalEvaluator.EvaluateValues(symbol, new(), primary, direction, (decimal)close, "15m");
        Assert.True(result.Long.Direction);
        Assert.False(result.Long.Momentum);
        Assert.False(result.Long.Ready);
        Assert.DoesNotContain(result.Long.FailedConditions, s => s.Contains("方向"));
    }

    [Fact]
    public void Round143FutureTargetsDoNotMeetNetRiskReward()
    {
        var risk = new RiskControlConfig();
        Assert.Equal(0.179688m, Math.Round(TradingRiskPolicy.NetRiskReward(2530.5m, 6.81m, 2536.5m, true, risk)!.Value, 6));
        Assert.Equal(0.759863m, Math.Round(TradingRiskPolicy.NetRiskReward(2530.5m, 6.81m, 2542.8m, true, risk)!.Value, 6));
    }

    [Fact]
    public void NearestStructureCannotBeSkippedToReachFarTarget()
    {
        var time = DateTime.UtcNow;
        var bars = new[] { 101m, 105m, 102m, 110m, 103m }.Select((high, i) =>
            new MarketKline(time.AddMinutes(i), 100, high, 99, 100, 1)).ToList();
        Assert.Equal(105m, SignalEvaluator.NearestTarget(bars, [], 100, true));
        Assert.Null(SignalEvaluator.NearestTarget(bars, [], 111, true));
    }

    [Fact]
    public void DefaultAndCustomEntryAreExplicitlyDistinguished()
    {
        var config = new StrategyConfig(); config.PromptSections.Entry = "自定义入场";
        var result = SignalEvaluator.Evaluate("BTC/USDT", config, null, null, DateTime.UtcNow);
        Assert.False(result.DefaultEntry);
        Assert.Null(result.Long.Direction);
        Assert.False(result.Long.Ready);
    }

    [Fact]
    public void ClosedBarsExcludeTheInProgressCandle()
    {
        var now = DateTime.UtcNow;
        var span = IndicatorCalculator.TryParseInterval("5m")!.Value;
        var aligned = new DateTime(now.Ticks / span.Ticks * span.Ticks, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, 100).Select(n => new MarketKline(aligned - span * (100 - n),
            100 + n, 102 + n, 99 + n, 101 + n, 1000)).ToList();
        bars.Add(new(aligned, 200, 300, 100, 250, 1000)); // 明确进行中的最后一根。
        var snapshot = new MarketSnapshot("live", "BTC/USDT", 250, bars, AsOfUtc: now);
        Assert.Equal(100, IndicatorCalculator.ClosedBars(snapshot.Klines, "5m", now).Count);
    }
}
