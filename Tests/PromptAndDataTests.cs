using CryptoExchange.Net.SharedApis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using System.Text.Json;
using Xunit;

namespace LuckyCat.Tests;

public class PromptAndDataTests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static SharedSymbol Symbol(string name) => new(TradingMode.PerpetualLinear, name, "USDT");
    private static MarketSnapshot Snapshot(string name, string frame = "5m", bool live = true)
    {
        var span = IndicatorCalculator.TryParseInterval(frame)!.Value;
        var aligned = new DateTime(Now.Ticks / span.Ticks * span.Ticks, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, 100).Select(n => new MarketKline(aligned - span * (100 - n),
            100 + n, 102 + n, 99 + n, 101 + n, 1000)).ToList();
        bars.Add(new(aligned, 200, 300, 100, 250, 1000)); // 明确进行中的最后一根。
        return new(live ? "live" : "missing", $"{name}/USDT", 250, bars, AsOfUtc: Now);
    }
    private static List<(StrategyTestRunner.SnapshotSeries Series, MarketSnapshot Snapshot)> Rows(string name) =>
        new[] {"5m", "15m", "1h"}.Select(t => (new StrategyTestRunner.SnapshotSeries(Symbol(name), t, 30, t == "5m"), Snapshot(name, t))).ToList();

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

    [Fact]
    public void HeldSymbolSurvivesExclusionAndCandidateLimit()
    {
        var config = new StrategyConfig();
        config.CoinSource.StaticCoins = Enumerable.Range(0, 15).Select(n => $"COIN{n}").ToList();
        config.CoinSource.ExcludedCoins = ["ETH"];
        var held = new[] {new PromptPosition("ETHUSDT", "多头", 1, 100, 0, 3, null, null)};
        var symbols = StrategyTestRunner.ResolveTestSymbols(config, null, held);
        Assert.Equal("ETH", symbols[0].BaseAsset);
        Assert.Equal(10, symbols.Count);
        var series = StrategyTestRunner.BuildSeries(symbols, ["5m", "15m", "1h"], config.Indicators.Klines);
        Assert.Contains(series, r => r.Symbol.BaseAsset == "ETH" && r.Timeframe == "1h");
        Assert.All(symbols, symbol => Assert.Contains(series, r => r.Symbol.BaseAsset == symbol.BaseAsset && r.Timeframe == "15m"));
    }

    [Fact]
    public void MissingOptionalOrUnrelatedSeriesDoesNotBlockValidTarget()
    {
        var rows = Rows("BTC");
        rows[2] = (rows[2].Series, rows[2].Snapshot with { Source = "missing" });
        rows.AddRange(Rows("ETH").Select(r => (r.Series, r.Snapshot with { Source = "missing" })));
        Assert.Equal(new[] {"BTC/USDT"}, StrategyTestRunner.EligibleSymbols(new(), rows, Now));
        rows[1] = (rows[1].Series, rows[1].Snapshot with { Source = "missing" });
        Assert.Empty(StrategyTestRunner.EligibleSymbols(new(), rows, Now));
    }

    [Fact]
    public void StaleSnapshotAndTickerFallbackAreNotExecutable()
    {
        var rows = Rows("BTC").Select(r => (r.Series, r.Snapshot with { AsOfUtc = Now.AddMinutes(-4) })).ToList();
        Assert.Empty(StrategyTestRunner.EligibleSymbols(new(), rows, Now));
        rows = Rows("BTC").Select(r => (r.Series, r.Snapshot with { TickerAvailable = false })).ToList();
        Assert.Empty(StrategyTestRunner.EligibleSymbols(new(), rows, Now));
    }

    [Fact]
    public void PromptHonorsRawAndVolumeFlagsAndRetainsCompleteTrigger()
    {
        var indicators = new IndicatorSettings { EnableRawKlines = false };
        indicators.Volume.Enabled = false;
        var trigger = new string('a', 160) + "且OI数量增加";
        var record = new DecisionRecord { ProviderName = "test", Model = "test", Action = "hold", CreatedAt = Now,
            ExecutionSummary = "未执行", DecisionJson = JsonSerializer.Serialize(new { reason = "test", trigger }) };
        var prompt = StrategyTestRunner.BuildUserPrompt(Rows("BTC"), ["5m", "15m", "1h"], new(), "binance", 1, [], [record],
            new Dictionary<string, SymbolSentiment>(), false, false, [], new(100, 100, "Gate", Now), [], null, indicators);
        Assert.DoesNotContain("| 时间 |", prompt);
        Assert.DoesNotContain("量（基准资产）", prompt);
        Assert.Contains("已收盘结构摘要", prompt);
        Assert.Contains(trigger, prompt);
        Assert.Contains("未执行", prompt);
        Assert.Contains("执行交易所 Gate", prompt);
        Assert.Contains("不是持仓事实", prompt);
    }

    [Fact]
    public void CandleClosureIsExplicitAndDoesNotUseFutureBarForIndicators()
    {
        var snapshot = Snapshot("BTC");
        Assert.Equal(100, IndicatorCalculator.ClosedBars(snapshot.Klines, "5m", Now).Count);
        var prompt = StrategyTestRunner.BuildUserPrompt(Rows("BTC"), ["5m", "15m", "1h"], new(), "binance", 1, [], [],
            new Dictionary<string, SymbolSentiment>(), false, false, [], null, [], null, new());
        Assert.Contains("进行中/暂报", prompt);
        Assert.Contains("已收盘", prompt);
        Assert.Contains("不能定量", prompt);
    }

    [Fact]
    public void CompressionKeepsHeldTimeframesAndIndicatorWarmup()
    {
        var held = new[] {new PromptPosition("BTC/USDT", "多头", 1, 100, 0, 3, null, null)};
        var rows = Rows("BTC"); rows.AddRange(Rows("ETH"));
        var provider = new Provider { Name = "test", Model = "test", BaseUrl = "http://invalid", Type = ProviderType.OpenAICompatible,
            ContextTokens = 100, AutoCompressContext = true };
        var result = StrategyTestRunner.ApplyContextGuard(provider, "system", rows, ["5m", "15m", "1h"], new(), "binance", 2,
            [], [], new Dictionary<string, SymbolSentiment>(), false, false, held, null, [], null, new());
        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, r => { Assert.Equal("BTC", r.Series.Symbol.BaseAsset); Assert.Equal(101, r.Snapshot.Klines.Count); });
        Assert.True(result.EstimatedTokens > provider.ContextTokens);
    }

    [Fact]
    public void DefaultPromptHasConcreteDirectionRoleAndNoTargetStretching()
    {
        var prompt = PromptComposer.Compose(new()).SystemPrompt;
        Assert.Contains("方向周期=15m", prompt);
        Assert.Contains("其他参考周期用于描述风险", prompt);
        Assert.Contains("不设置每日最低交易笔数", prompt);
        Assert.Contains("不是胜率", prompt);
        Assert.DoesNotContain("而不是因此放弃机会", prompt);
        var config = new StrategyConfig(); config.Indicators.Klines.EnableMultiTimeframe = false;
        Assert.Contains("只允许 hold/close", PromptComposer.Compose(config).SystemPrompt);
    }

    [Fact]
    public async Task SqliteMemoryIsScopedToTraderAndPreservesLongDecisionJson()
    {
        // 仅新建进程内 SQLite，不连接项目运行数据库。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var memory = new DecisionMemoryService(db, new SseEventHub());
        var provider = new Provider { Name = "test", Model = "test", BaseUrl = "http://invalid", Type = ProviderType.OpenAICompatible };
        var trigger = new string('x', 2500);
        var d = JsonSerializer.SerializeToElement(new { action = "hold", confidence = 80, reason = "test", trigger });
        await memory.RecordAsync(1, 10, provider, d, null, null, null, null, null, null, default);
        await memory.RecordAsync(1, 20, provider, d, null, null, null, null, null, null, default);
        await memory.RecordAsync(1, null, provider, d, null, null, null, null, null, null, default);
        var records = await memory.LoadRecentAsync(1, default, 10);
        Assert.Single(records);
        Assert.Equal(10, records[0].TraderId);
        Assert.Equal(trigger, JsonDocument.Parse(records[0].DecisionJson).RootElement.GetProperty("trigger").GetString());
        Assert.Contains("未执行", (await memory.LoadRecentAsync(1, default))[0].ExecutionSummary);
    }

    [Fact]
    public async Task RiskExitBypassesUnrelatedMissingMarketButOpenDoesNot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var hub = new SseEventHub();
        var runner = new TraderRunnerService(scopes, new PositionHistorySyncService(scopes, hub), hub);
        var trader = new Trader { Id = 1, Name = "test", ExecutionMode = "dry" };
        var account = new ExchangeAccount { ExchangeType = ExchangeType.Binance, AccountName = "test", ApiKey = "test", ApiSecret = "test" };
        var close = JsonSerializer.SerializeToElement(new { action = "close", symbol = "BTC/USDT", confidence = 0, reason = "前提破坏" });
        var result = new StrategyTestResultDto(0, "", "", null, close, "incomplete", 0, null, null);
        await runner.ExecuteAsync(db, trader, account, new(), result, new(true, 100, 100, null), new(), default);
        Assert.Equal("close", db.ChangeTracker.Entries<TraderOrder>().Single().Entity.Action);
        db.ChangeTracker.Clear();
        result = result with { Decision = TradingRiskPolicyTests.Decision() };
        await runner.ExecuteAsync(db, trader, account, new(), result, new(true, 100, 100, null), new(), default);
        var audit = db.ChangeTracker.Entries<TraderOrder>().Single().Entity;
        Assert.False(audit.Success);
        Assert.Contains("禁止开仓", audit.Note);
    }

    [Fact]
    public void FloatingProfitDoesNotEraseUsedMargin()
    {
        var positions = new[] {new SymbolPosition("BTCUSDT", SharedPositionSide.Long, 1, 100, 50, null, 5)};
        Assert.Equal(20, TraderRunnerService.UsedMargin(new(true, 100, 150, null), positions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultSignalFailureRejectsModelOpenButCustomEntryRemainsIndependent(bool custom)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var hub = new SseEventHub();
        var runner = new TraderRunnerService(scopes, new PositionHistorySyncService(scopes, hub), hub);
        var trader = new Trader { Id = 1, Name = "test", ExecutionMode = "dry" };
        var account = new ExchangeAccount { ExchangeType = ExchangeType.Binance, AccountName = "test", ApiKey = "test", ApiSecret = "test" };
        var config = new StrategyConfig();
        config.PromptSections.Entry = custom ? "自定义规则" : PromptComposer.EntryDefault;
        var notReady = new SignalSideAssessment(true, false, true, true, 100, 1, 103, 2, ["动能未满足"]);
        var signal = new SignalAssessment("BTC/USDT", "15m", !custom, notReady, notReady);
        var result = new StrategyTestResultDto(0, "", "", null, TradingRiskPolicyTests.Decision(), "live", 0, null, null,
            ExecutableSymbols: ["BTC/USDT"], MarketAsOf: DateTime.UtcNow,
            SignalAssessments: new Dictionary<string, SignalAssessment> { ["BTC/USDT"] = signal });
        await runner.ExecuteAsync(db, trader, account, config, result, new(true, 100, 100, null), new(), default);
        var audit = db.ChangeTracker.Entries<TraderOrder>().Single().Entity;
        Assert.Equal(custom, audit.Success);
        Assert.Contains(custom ? "演练模式" : "判据未齐备", audit.Note);
    }
}
