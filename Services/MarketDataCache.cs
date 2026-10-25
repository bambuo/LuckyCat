using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// 单周期行情缓存（DI 作用域内一个实例，周期结束即释放；无静态缓存）：
/// 同一交易周期内多个工具/阶段重复读取同一份行情时只拉取一次，保证口径一致并减少交易所请求。
/// 缓存键包含交易所/标的/周期/根数/预热深度，不同参数不会串用。
/// </summary>
public sealed class MarketDataCache(IMarketDataReader reader)
{
    private readonly Dictionary<string, MarketSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketSnapshot?> _overviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, SymbolSentiment>> _sentiments = new(StringComparer.Ordinal);
    private IReadOnlyList<ExternalDataResult>? _news;
    private CoinSourceResolution? _candidates;

    public async Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
        int bars, int warmupBars, CancellationToken ct)
    {
        var key = $"{exchange}|{symbol.BaseAsset}|{timeframe}|{bars}|{warmupBars}";
        if (_snapshots.TryGetValue(key, out var hit)) return hit;
        var value = await reader.GetSnapshotAsync(exchange, symbol, timeframe, bars, warmupBars, ct);
        _snapshots[key] = value;
        return value;
    }

    public async Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct)
    {
        if (_overviews.TryGetValue(exchange, out var hit)) return hit;
        var value = await reader.GetBtcOverviewAsync(exchange, ct);
        _overviews[exchange] = value;
        return value;
    }

    public async Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
        IReadOnlyList<SharedSymbol> symbols, bool oi, bool funding, CancellationToken ct)
    {
        var key = $"{exchange}|{string.Join(',', symbols.Select(s => s.BaseAsset))}|{oi}|{funding}";
        if (_sentiments.TryGetValue(key, out var hit)) return hit;
        var value = await reader.GetSentimentAsync(exchange, symbols, oi, funding, ct);
        _sentiments[key] = value;
        return value;
    }

    public async Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(IEnumerable<ExternalDataSource> sources, CancellationToken ct)
        => _news ??= await reader.GetNewsAsync(sources, ct);

    public async Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct)
        => _candidates ??= await reader.GetCandidatesAsync(config, ct);
}
