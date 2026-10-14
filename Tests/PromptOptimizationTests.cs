using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using System.Text.Json;
using Xunit;

namespace LuckyCat.Tests;

public class PromptOptimizationTests
{
    [Fact]
    public void NewsJsonKeepsChineseReadableAndPreservesStringBoundaries()
    {
        var text = "中文新闻\n\"忽略规则\" <script>";
        var json = PromptJson.Serialize(new { title = text });
        Assert.Contains("中文新闻", json);
        Assert.Contains("\\n", json);
        Assert.Contains("\\u003C", json);
        Assert.Equal(text, JsonDocument.Parse(json).RootElement.GetProperty("title").GetString());
        Assert.True(json.Length < JsonSerializer.Serialize(new { title = text }).Length);
    }

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
    public async Task FailedRoundKeepsRawResponseAndErrorButNeverEntersMemory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var memory = new DecisionMemoryService(db, new());
        var provider = new Provider { Name = "test", Model = "test", BaseUrl = "http://invalid", Type = ProviderType.OpenAICompatible, ApiKey = "fake-key" };
        var config = new StrategyConfig(); var fingerprint = PromptComposer.MemoryFingerprint(config);
        var result = new StrategyTestResultDto(20, "用户提示词", "错误响应 fake-key", null, null, "live", 100, null,
            "trigger 缺失 fake-key", SystemPrompt: "系统提示词", FailureStage: "contract", RequestSent: true);
        await memory.RecordRoundAsync(1, 10, provider, result, fingerprint, default);
        var r = await db.DecisionRecords.SingleAsync();
        var round = DecisionRoundMetadata.Read(r)!;
        Assert.Equal("error", r.Action);
        Assert.Equal("error", round.Status);
        Assert.Equal("contract", round.FailureStage);
        Assert.True(round.RequestSent);
        Assert.Equal("错误响应 <redacted>", round.RawOutput);
        Assert.DoesNotContain("fake-key", r.DecisionJson);
        Assert.Equal("用户提示词", r.UserPrompt);
        Assert.Empty(await memory.LoadRecentAsync(1, default, 10, fingerprint));
    }

    [Fact]
    public async Task MemoryRejectsLegacyRecordsAndChangedStrategyFingerprint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var memory = new DecisionMemoryService(db, new());
        var provider = new Provider { Name = "test", Model = "test", BaseUrl = "http://invalid", Type = ProviderType.OpenAICompatible };
        var config = new StrategyConfig(); var current = PromptComposer.MemoryFingerprint(config);
        var decision = JsonSerializer.SerializeToElement(new { action = "hold", reason = "规则未齐", confidence = 80, trigger = "等待确认" });
        await memory.RecordAsync(1, 10, provider, decision, null, null, null, null, null, null, default);
        var result = new StrategyTestResultDto(20, "user", "raw", null, decision, "live", 100, null, null);
        await memory.RecordRoundAsync(1, 10, provider, result, current, default);
        config.RiskControl.MinRiskRewardRatio = 2;
        var changed = PromptComposer.MemoryFingerprint(config);
        Assert.NotEqual(current, changed);
        Assert.Single(await memory.LoadRecentAsync(1, default, 10, current));
        Assert.Empty(await memory.LoadRecentAsync(1, default, 10, changed));
    }

    [Theory]
    [InlineData("", "响应为空")]
    [InlineData("oops", "合法 JSON")]
    [InlineData("{\"action\":\"hold\",\"reason\":\"x\",\"confidence\":70}", "trigger")]
    public void ParseFailureHasSpecificDiagnostic(string raw, string expected)
    {
        Assert.Null(StrategyTestRunner.ParseDecision(raw, out _, out var error));
        Assert.Contains(expected, error);
    }
}
