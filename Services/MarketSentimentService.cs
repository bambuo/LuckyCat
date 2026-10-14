using CryptoExchange.Net.SharedApis;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace LuckyCat.Services;

/// <summary>单标的行情情绪数据：持仓量名义价值（USDT）与 1h 数量变化率（另保留完整数量/名义价值观察）、当前资金费率（%）、下次资金费结算时刻（UTC）；
/// null = 该字段本轮缺席。</summary>
public record OiObservation(decimal? Quantity, decimal? Usd, decimal? QuantityChangePct,
    decimal? ValueChangePct, DateTime? WindowStartUtc, DateTime? WindowEndUtc, DateTime FetchedAtUtc);

public record SymbolSentiment(decimal? OiUsd, decimal? OiChangePct, decimal? FundingRatePct, DateTime? NextFundingAt,
    OiObservation? Oi = null, DateTime? FundingFetchedAtUtc = null);

/// <summary>
/// 市场情绪数据源（AI 测试「行情快照」逐标的注入用）：Binance / Gate.io 公开 REST（与 OI 榜同一实现口径）。
/// 各开关控制对应数据的拉取——关闭即不请求；结果缓存 5 分钟（失败短缓存 20s），单标的失败不影响其他标的。
/// </summary>
public class MarketSentimentService(IHttpClientFactory httpClientFactory, CoinRankingService ranking)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(20);

    private static readonly ConcurrentDictionary<string, OiObservation> OiCache = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, decimal? RatePct, DateTime? NextFundingAt)> FundingCache = new();

    /// <summary>逐标的拉取（并行）；返回以基准资产为键的情绪数据（未启用/解析失败时字段为 null）。</summary>
    public async Task<IReadOnlyDictionary<string, SymbolSentiment>> GetAsync(
        string exchange,
        IReadOnlyList<SharedSymbol> symbols,
        bool includeOi,
        bool includeFunding,
        CancellationToken ct)
    {
        var result = new Dictionary<string, SymbolSentiment>(StringComparer.Ordinal);
        if (!includeOi && !includeFunding) return result;

        var tasks = symbols.Select(symbol => FetchSymbolAsync(exchange, symbol, includeOi, includeFunding, ct)).ToList();
        foreach (var (baseAsset, value) in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            result[baseAsset] = value;
        }
        return result;
    }

    private async Task<(string BaseAsset, SymbolSentiment Value)> FetchSymbolAsync(
        string exchange,
        SharedSymbol symbol,
        bool includeOi,
        bool includeFunding,
        CancellationToken ct)
    {
        // 公开 REST 使用交易所原生合约名：先按值对象在 USDT 永续目录中解析，不做字符串拼装
        var futuresSymbol = await ranking.ResolveFuturesSymbolAsync(exchange, symbol, ct).ConfigureAwait(false);
        if (futuresSymbol is null)
        {
            Console.WriteLine($"[MarketSentiment] {exchange} 目录中无 {symbol.BaseAsset}/{symbol.QuoteAsset}，本轮缺席");
            return (symbol.BaseAsset, new SymbolSentiment(null, null, null, null));
        }

        try
        {
            // 突发超时的重试与超时预算由全局弹性管道负责（Program.cs · AddStandardResilienceHandler）
            var oiTask = includeOi
                ? GetOiAsync(exchange, futuresSymbol.Name, ct)
                : Task.FromResult<OiObservation?>(null);
            var fundingTask = includeFunding
                ? GetFundingAsync(exchange, futuresSymbol.Name, ct)
                : Task.FromResult<(decimal? RatePct, DateTime? NextAt)>((null, null));

            await Task.WhenAll(oiTask, fundingTask).ConfigureAwait(false);
            return (symbol.BaseAsset, new SymbolSentiment(oiTask.Result?.Usd, oiTask.Result?.QuantityChangePct, fundingTask.Result.RatePct, fundingTask.Result.NextAt,
                oiTask.Result, FundingCache.GetValueOrDefault($"{exchange}:{futuresSymbol.Name}").At));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"[MarketSentiment] {exchange} {futuresSymbol.Name} 拉取超时，本轮缺席");
            return (symbol.BaseAsset, new SymbolSentiment(null, null, null, null));
        }
    }

    // 简单 GET（重试/超时由全局弹性管道负责；失败返回 null，外层撤消如实上抛）
    private async Task<string?> TryGetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient();
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MarketSentiment] {url} 拉取异常：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    // 数量变化与名义价值变化分别计算，保留交易所窗口和实际采集时间；缓存命中不改时间。
    private async Task<OiObservation?> GetOiAsync(string exchange, string name, CancellationToken ct)
    {
        var key = $"{exchange}:{name}";
        if (OiCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAtUtc < CacheTtl)
            return cached;
        var url = exchange == "gate"
            ? $"https://api.gateio.ws/api/v4/futures/usdt/contract_stats?contract={Uri.EscapeDataString(name)}&interval=1h&limit=2"
            : $"https://fapi.binance.com/futures/data/openInterestHist?symbol={Uri.EscapeDataString(name)}&period=1h&limit=2";
        var body = await TryGetStringAsync(url, ct).ConfigureAwait(false);
        if (body is null) return null;
        try
        {
            var result = ParseOi(body, exchange, DateTime.UtcNow);
            if (result is not null) OiCache[key] = result;
            return result;
        }
        catch (JsonException) { return null; }
    }

    public static OiObservation? ParseOi(string body, string exchange, DateTime fetchedAtUtc)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var gate = exchange == "gate";
        var timeField = gate ? "time" : "timestamp";
        var items = doc.RootElement.EnumerateArray()
            .Where(i => ReadDecimal(i, timeField) is > 0)
            .OrderBy(i => ReadDecimal(i, timeField)).ToList();
        if (items.Count < 2) return null;
        var first = items[^2];
        var last = items[^1];
        DateTime Time(JsonElement item) => DateTime.UnixEpoch.AddMilliseconds((double)ReadDecimal(item, timeField)!.Value * (gate ? 1000d : 1d));
        var start = Time(first);
        var end = Time(last);
        if (end <= start || end - start != TimeSpan.FromHours(1) || end > fetchedAtUtc.AddMinutes(1)) return null;
        var quantityField = gate ? "open_interest" : "sumOpenInterest";
        var valueField = gate ? "open_interest_usd" : "sumOpenInterestValue";
        var quantity = ReadDecimal(last, quantityField);
        var usd = ReadDecimal(last, valueField);
        if (quantity is < 0 || usd is < 0) return null;
        decimal? Change(string field) => ReadDecimal(first, field) is > 0 and var old && ReadDecimal(last, field) is >= 0 and var current
            ? Math.Round((current / old - 1m) * 100m, 2) : null;
        return new(quantity, usd, Change(quantityField), Change(valueField), start, end, fetchedAtUtc);
    }

    // 资金费率：当前值（交易所原生约定为小数，统一换算为百分点）+ 下次结算时刻（UTC；Binance 毫秒、Gate 秒）
    private async Task<(decimal? RatePct, DateTime? NextAt)> GetFundingAsync(string exchange, string name, CancellationToken ct)
    {
        var key = $"{exchange}:{name}";
        if (FundingCache.TryGetValue(key, out var cached)
            && DateTime.UtcNow - cached.At < (cached.RatePct is not null ? CacheTtl : FailureCacheTtl)
            && (cached.NextFundingAt is null || cached.NextFundingAt > DateTime.UtcNow))
        {
            return (cached.RatePct, cached.NextFundingAt);
        }

        var url = exchange == "gate"
            ? $"https://api.gateio.ws/api/v4/futures/usdt/contracts/{Uri.EscapeDataString(name)}"
            : $"https://fapi.binance.com/fapi/v1/premiumIndex?symbol={Uri.EscapeDataString(name)}";
        var rateField = exchange == "gate" ? "funding_rate" : "lastFundingRate";
        var nextField = exchange == "gate" ? "funding_next_apply" : "nextFundingTime";
        var epochToMs = exchange == "gate" ? 1000m : 1m;

        decimal? ratePct = null;
        DateTime? nextAt = null;
        var body = await TryGetStringAsync(url, ct).ConfigureAwait(false);
        if (body is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (ReadDecimal(doc.RootElement, rateField) is { } rate)
                    {
                        ratePct = Math.Round(rate * 100m, 4);
                    }
                    if (ReadDecimal(doc.RootElement, nextField) is { } next && next > 0)
                    {
                        nextAt = DateTime.UnixEpoch.AddMilliseconds((double)(next * epochToMs));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MarketSentiment] {name} 资金费率解析异常：{ex.GetType().Name} {ex.Message}");
            }
        }

        FundingCache[key] = (DateTime.UtcNow, ratePct, nextAt);
        return (ratePct, nextAt);
    }

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }
}
