using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LuckyCat.Tests;

public class MarketToolsTests
{
    [Fact]
    public async Task Klines_are_cached_per_cycle()
    {
        var reader = new FakeReader();
        var cache = new MarketDataCache(reader);
        var tools = new MarketTools(cache, new StrategyConfig(), new HashSet<string>(["BTC/USDT"]));
        await tools.GetKlinesAsync("BTC/USDT", "1h", 60, false, default);
        await tools.GetKlinesAsync("BTC/USDT", "1h", 60, false, default);
        Assert.Equal(1, reader.SnapshotCalls);
    }

    [Fact]
    public async Task Unknown_symbol_is_rejected()
    {
        var tools = new MarketTools(new MarketDataCache(new FakeReader()), new StrategyConfig(), new HashSet<string>(["BTC/USDT"]));
        var result = await tools.GetKlinesAsync("ETH/USDT", "1h", 60, false, default);
        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.SymbolNotAllowed, result.Error!.Code);
    }

    [Fact]
    public void Oversized_success_is_truncated_to_16kib()
    {
        var result = ToolEnvelope.Success(new string('x', 20_000), null, "test");

        Assert.True(result.Ok);
        Assert.True(result.Truncated);
        Assert.Equal("[truncated]", result.Data!.Value.GetString());
        Assert.Contains("16 KiB", result.Warnings[0]);
    }

    [Fact]
    public async Task Invalid_bars_is_rejected()
    {
        var tools = new MarketTools(new MarketDataCache(new FakeReader()), new StrategyConfig(), new HashSet<string>(["BTC/USDT"]));
        var result = await tools.GetKlinesAsync("BTC/USDT", "1h", 500, false, default);
        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task All_six_market_tools_return_envelopes()
    {
        var config = new StrategyConfig
        {
            Indicators =
            {
                ExternalDataSources = [new ExternalDataSource { Name = "wire", Url = "https://example.invalid/feed" }]
            }
        };
        var tools = new MarketTools(new MarketDataCache(new RichReader()), config, new HashSet<string>(["BTC/USDT"]));

        Assert.True((await tools.GetCoinCandidatesAsync()).Ok);
        Assert.True((await tools.GetMarketOverviewAsync()).Ok);
        Assert.True((await tools.GetKlinesAsync("BTC/USDT", "1h", 60, false, default)).Ok);
        Assert.True((await tools.GetIndicatorsAsync("BTC/USDT", "1h", 60, default)).Ok);
        Assert.True((await tools.GetSentimentAsync(["BTC/USDT"], true, true, default)).Ok);

        var news = await tools.GetNewsAsync(10, default);
        Assert.True(news.Ok);
        Assert.StartsWith("<untrusted>", news.Data!.Value.GetString());
        Assert.EndsWith("</untrusted>", news.Data!.Value.GetString());
    }

    [Fact]
    public async Task Auditor_persists_invocation_enforces_budget_and_publishes_failure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var hub = new SseEventHub();
        var (events, subscription) = hub.Subscribe(SseChannels.Trader(7));
        using var _ = subscription;
        var auditor = new ToolAuditor(db, hub, new DenyBudget(), Ids.NewUlid(), Ids.NewUlid(), 7);

        var result = await auditor.RunAsync("get_klines", "analyst", new { symbol = "BTC/USDT" },
            () => Task.FromResult(ToolEnvelope.Success("rows", DateTimeOffset.UtcNow, "live")), default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ToolBudgetExceeded, result.Error!.Code);
        var row = await db.ToolInvocations.SingleAsync();
        Assert.Equal("get_klines", row.ToolName);
        Assert.False(row.Success);
        Assert.Equal(ToolErrors.ToolBudgetExceeded, row.ErrorCode);
        Assert.True(events.TryRead(out var evt));
        Assert.Equal("tool", evt!.Name);
    }

    private sealed class DenyBudget : ICycleBudget
    {
        public bool TryConsumeTool(string role) => false;
    }

    private sealed class RichReader : IMarketDataReader
    {
        private static readonly DateTime Now = DateTime.UtcNow;

        public Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
            int bars, int warmupBars, CancellationToken ct)
        {
            var rows = Enumerable.Range(0, 120).Select(i =>
                new MarketKline(Now.AddHours(i - 120), 100, 101, 99, 100, 10)).ToList();
            return Task.FromResult(new MarketSnapshot("live", "BTC/USDT", 100, rows, AsOfUtc: Now));
        }

        public Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) =>
            Task.FromResult<MarketSnapshot?>(new MarketSnapshot("live", "BTC/USDT", 50_000, [],
                ChangePct24h: 2.5m, High24h: 51_000, Low24h: 48_000, QuoteVolume24h: 1_000_000_000m, AsOfUtc: Now));

        public Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
            IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, SymbolSentiment>>(new Dictionary<string, SymbolSentiment>
            {
                ["BTC"] = new(1_000_000m, 1.5m, 0.01m, Now.AddHours(4))
            });

        public Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(IEnumerable<ExternalDataSource> sources, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExternalDataResult>>(
                [new ExternalDataResult("wire", [new ExternalDataItem("10-25 10:00", "标题", "内容")], null, Now)]);

        public Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct) =>
            Task.FromResult<CoinSourceResolution?>(new CoinSourceResolution(
                [new SharedSymbol(TradingMode.PerpetualLinear, "BTC", "USDT")], [], [], []));
    }

    private sealed class FakeReader : IMarketDataReader
    {
        public int SnapshotCalls;
        public Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
            int bars, int warmupBars, CancellationToken ct)
        {
            SnapshotCalls++;
            var now = DateTime.UtcNow;
            var barsData = Enumerable.Range(0, 80).Select(i =>
                new MarketKline(now.AddHours(i - 80), 100, 101, 99, 100, 10)).ToList();
            return Task.FromResult(new MarketSnapshot("live", symbol.BaseAsset + "/USDT", 100, barsData, AsOfUtc: now));
        }
        public Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) => Task.FromResult<MarketSnapshot?>(null);
        public Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
            IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, SymbolSentiment>>(new Dictionary<string, SymbolSentiment>());
        public Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(IEnumerable<ExternalDataSource> sources, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExternalDataResult>>([]);
        public Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct) =>
            Task.FromResult<CoinSourceResolution?>(null);
    }
}
