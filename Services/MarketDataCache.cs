using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// 单周期行情缓存（DI 作用域内一个实例，周期结束即释放；无静态缓存）：
/// 同一交易周期内多个工具/阶段重复读取同一份行情时只拉取一次，保证口径一致并减少交易所请求。
/// 只缓存成功且可用的结果——失败（missing 快照 / null 概览 / 全空情绪 / 全源缺席新闻）不落缓存，
/// 模型按 retryable 重试时能真正重新拉取，而不是命中同一份失败结果。
/// 情绪按标的逐条返回（字段可能全为 null），只有至少一个标的出现工具可展示的数据时才缓存；
/// 部分标的缺席的结果仍缓存（工具以 incomplete 呈现，不再给出重试暗示）。
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
        // 情绪服务会为每个请求标的插入一条记录（即使字段全为 null）：只有出现工具可展示的
        // OI 名义价值或资金费率时才缓存；全空结果不缓存，retryable 失败的重试能真正重新拉取。
        if (value.Values.Any(sentiment => HasData(sentiment, oi, funding))) _sentiments[key] = value;
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

    // 与 MarketTools.GetSentimentCoreAsync 的可展示字段口径一致：只有请求的 OI 名义价值或资金费率
    // 至少一个存在时才算有数据；仅采集时间/窗口等附属字段不可展示，不计入。
    private static bool HasData(SymbolSentiment sentiment, bool includeOi, bool includeFunding) =>
        (includeOi && sentiment.OiUsd is not null)
        || (includeFunding && sentiment.FundingRatePct is not null);
}
