using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// <see cref="IMarketDataReader"/> 的真实实现：行情快照/情绪/币源仍为转发（重试、缓存、币源解析口径不变）；
/// 新闻经 <see cref="CryptoNewsAggregator"/> 拉取内置源并合并去重。
/// </summary>
public sealed class MarketDataReader(
    MarketSnapshotService snapshots,
    MarketSentimentService sentiment,
    CoinRankingService ranking,
    CryptoNewsAggregator news) : IMarketDataReader
{
    public Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
        int bars, int warmupBars, CancellationToken ct) =>
        snapshots.GetAsync(exchange, symbol, timeframe, bars, ct, computeBars: warmupBars);

    public Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) =>
        snapshots.GetBtcOverviewAsync(exchange, ct);

    public Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
        IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct) =>
        sentiment.GetAsync(exchange, symbols, includeOi, includeFunding, ct);

    public Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(CancellationToken ct) =>
        news.FetchAsync(ct);

    public Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct) =>
        ranking.ResolveAsync(config, ct);
}
