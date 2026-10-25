using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// 单周期行情缓存（DI 作用域内一个实例，周期结束即释放；无静态缓存）：
/// 同一交易周期内多个工具/阶段重复读取同一份行情时只拉取一次，保证口径一致并减少交易所请求。
/// 只缓存成功且非空的结果——失败（missing 快照 / null 概览 / 空情绪 / 全源缺席新闻）不落缓存，
/// 模型按 retryable 重试时能真正重新拉取，而不是命中同一份失败结果。
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
        if (value.Source == "live") _snapshots[key] = value;
        return value;
    }

    public async Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct)
    {
        if (_overviews.TryGetValue(exchange, out var hit)) return hit;
        var value = await reader.GetBtcOverviewAsync(exchange, ct);
        if (value is { Source: "live" }) _overviews[exchange] = value;
        return value;
    }

    public async Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
        IReadOnlyList<SharedSymbol> symbols, bool oi, bool funding, CancellationToken ct)
    {
        var key = $"{exchange}|{string.Join(',', symbols.Select(s => s.BaseAsset))}|{oi}|{funding}";
        if (_sentiments.TryGetValue(key, out var hit)) return hit;
        var value = await reader.GetSentimentAsync(exchange, symbols, oi, funding, ct);
        if (value.Count > 0) _sentiments[key] = value;
        return value;
    }

    public async Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(IEnumerable<ExternalDataSource> sources, CancellationToken ct)
    {
        if (_news is { } hit) return hit;
        var value = await reader.GetNewsAsync(sources, ct);
        if (value.Any(result => result.Problem is null && result.Items.Count > 0)) _news = value;
        return value;
    }

    public async Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct)
    {
        if (_candidates is { } hit) return hit;
        var value = await reader.GetCandidatesAsync(config, ct);
        if (value is not null && (value.Score.Count > 0 || value.OiTop.Count > 0
            || value.OiLow.Count > 0 || value.Merged.Count > 0))
        {
            _candidates = value;
        }
        return value;
    }
}
