using System.Text.Json;
using LuckyCat.Services;
using Xunit;

namespace LuckyCat.Tests;

/// <summary>
/// 从旧 <c>PromptAndDataTests</c> 迁出的存续用例：OI 口径解析仍由新链路
/// <see cref="MarketSentimentService"/> 使用（Task 18 删除旧提示词链路后保留覆盖）。
/// </summary>
public class MarketSentimentTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public void OiSeparatesConstantQuantityFromRisingDollarValueAndSortsTime()
    {
        var end = new DateTimeOffset(Now.AddMinutes(-5)).ToUnixTimeMilliseconds();
        var raw = JsonSerializer.Serialize(new[] {
            new { timestamp = end, sumOpenInterest = "100", sumOpenInterestValue = "1200" },
            new { timestamp = end - 3600000, sumOpenInterest = "100", sumOpenInterestValue = "1000" }
        });
        var oi = MarketSentimentService.ParseOi(raw, "binance", Now);
        Assert.NotNull(oi);
        Assert.Equal(0m, oi.QuantityChangePct);
        Assert.Equal(20m, oi.ValueChangePct);
        Assert.Equal(TimeSpan.FromHours(1), oi.WindowEndUtc - oi.WindowStartUtc);
        Assert.Equal(Now, oi.FetchedAtUtc);
    }

    [Fact]
    public void GateTimestampUsesSecondsAndQuantityUsesContracts()
    {
        var end = new DateTimeOffset(Now.AddMinutes(-5)).ToUnixTimeSeconds();
        var raw = JsonSerializer.Serialize(new[] {
            new { time = end - 3600, open_interest = 200, open_interest_usd = 2000 },
            new { time = end, open_interest = 180, open_interest_usd = 2100 }
        });
        var oi = MarketSentimentService.ParseOi(raw, "gate", Now);
        Assert.Equal(-10m, oi!.QuantityChangePct);
        Assert.Equal(5m, oi.ValueChangePct);
    }

    [Fact]
    public void MissingOrWrongOiWindowCannotBePresentedAsOneHourChange()
    {
        Assert.Null(MarketSentimentService.ParseOi("[]", "binance", Now));
        Assert.Null(MarketSentimentService.ParseOi("[{\"sumOpenInterest\":\"1\"},{\"sumOpenInterest\":\"2\"}]", "binance", Now));
        Assert.Null(MarketSentimentService.ParseOi("[{\"timestamp\":1000},{\"timestamp\":2000}]", "binance", Now));
    }
}
