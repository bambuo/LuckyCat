using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// <see cref="IMarketDataReader"/> 的真实实现：仅做转发，行为完全沿用既有服务
/// （行情快照的重试/缺失标注、情绪数据的 5 分钟缓存、外部数据源的 TTL 缓存、币源解析的口径均保持不变）。
/// </summary>
public sealed class MarketDataReader(
    MarketSnapshotService snapshots,
    MarketSentimentService sentiment,
    ExternalDataSourceService externalData,
    CoinRankingService ranking) : IMarketDataReader
{
    public Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
        int bars, int warmupBars, CancellationToken ct) =>
        snapshots.GetAsync(exchange, symbol, timeframe, bars, ct, computeBars: warmupBars);

    public Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) =>
        snapshots.GetBtcOverviewAsync(exchange, ct);

    public Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
        IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct) =>
        sentiment.GetAsync(exchange, symbols, includeOi, includeFunding, ct);

    public Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(IEnumerable<ExternalDataSource> sources, CancellationToken ct) =>
        externalData.FetchAllAsync(sources, ct);

    public Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct) =>
        ranking.ResolveAsync(config, ct);
}
