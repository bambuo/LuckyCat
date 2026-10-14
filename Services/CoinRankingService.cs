using CryptoClients.Net;
using CryptoClients.Net.Clients;
using CryptoClients.Net.Models;
using CryptoExchange.Net.SharedApis;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>OI 榜条目：标的（值对象）+ 1 小时 OI 百分比变化。</summary>
public record OiMover(SharedSymbol Symbol, decimal ChangePct);

/// <summary>币种来源解析结果（全部为 SharedSymbol 值对象：标的 + 计价币两个字段）。</summary>
public record CoinSourceResolution(
    IReadOnlyList<SharedSymbol> Score,
    IReadOnlyList<OiMover> OiTop,
    IReadOnlyList<OiMover> OiLow,
    IReadOnlyList<SharedSymbol> Merged)
{
    // 提示词展示：评分榜/混合用基准币名；OI 榜附 1h 变化率（如 SOL(+1.2%)）
    public IReadOnlyList<string> ScoreDisplay => Score.Select(s => s.BaseAsset).ToList();
    public IReadOnlyList<string> OiTopDisplay => OiTop.Select(m => Format(m.Symbol, m.ChangePct)).ToList();
    public IReadOnlyList<string> OiLowDisplay => OiLow.Select(m => Format(m.Symbol, m.ChangePct)).ToList();
    public IReadOnlyList<string> MergedDisplay => Merged.Select(s => s.BaseAsset).ToList();

    private static string Format(SharedSymbol symbol, decimal changePct) =>
        $"{symbol.BaseAsset}({changePct:+0.0;-0.0}%)";
}

/// <summary>
/// 币种候选来源解析（真实实现）：
///  - 综合评分榜：标的目录（`SharedFuturesSymbol` 值对象）与全市场 24h 行情按交易所原生符号 Join，
///    「24h 成交额 60% + |涨跌幅| 40%」分位加权取前 N；
///  - OI 持仓增加/减少榜：成交额前 {OiScanUniverse} 扫描集，逐币拉 1 小时 OI 历史算百分比变化
///    （共享 API 只提供当前 OI，历史走交易所原生接口），按变化率降序/升序取前 N；
///  - 混合模式：各启用来源合并去重（顺序：评分榜 → OI增 → OI减 → 自定义），上限 = 各来源 limit 的最大值。
/// 候选口径不按标的类型过滤（贵金属 / 股票类永续一视同仁，用户确认）；失败回退空列表（调用方回退为通用描述）。
/// </summary>
public class CoinRankingService(IHttpClientFactory httpClientFactory)
{
    // 口径常量（用户确认：1 小时窗 / 百分比变化 / 成交额前 100 扫描；评分权重为分位加权）
    private const int OiScanUniverse = 100;
    private const int OiConcurrency = 10;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OiScanTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OiCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(20);

    private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<UniverseEntry> Entries)> UniverseCache = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<OiMover> Movers)> OiCache = new();

    /// <summary>按策略配置解析币种来源；网格类型 / 静态列表 / 来源关闭时返回 null（走原有静态路径）。
    /// 「排除币种」在此处真实生效：各来源候选在取前 N 之前先剔除排除项（按基准资产匹配）。</summary>
    public async Task<CoinSourceResolution?> ResolveAsync(StrategyConfig config, CancellationToken ct)
    {
        if (config.StrategyType == "grid_trading") return null;

        var cs = config.CoinSource;
        var excluded = BuildExcludedSet(cs.ExcludedCoins);
        if (cs.SourceType == "score")
        {
            if (!cs.UseScore) return null;
            return new CoinSourceResolution(await GetScoreAsync(cs.Exchange, cs.ScoreLimit, excluded, ct), [], [], []);
        }
        if (cs.SourceType == "oi_top")
        {
            if (!cs.UseOiTop) return null;
            return new CoinSourceResolution([], await GetOiRankedAsync(cs.Exchange, cs.OiTopLimit, up: true, excluded, ct), [], []);
        }
        if (cs.SourceType == "oi_low")
        {
            if (!cs.UseOiLow) return null;
            return new CoinSourceResolution([], [], await GetOiRankedAsync(cs.Exchange, cs.OiLowLimit, up: false, excluded, ct), []);
        }
        if (cs.SourceType == "mixed")
        {
            return await ResolveMixedAsync(cs, excluded, ct);
        }
        return null; // static / 未知来源
    }

    /// <summary>排除币种集合：按基准资产匹配（容忍带计价后缀的写法，如 DOGE/USDT）。</summary>
    public static HashSet<string> BuildExcludedSet(IEnumerable<string> excludedCoins) =>
        excludedCoins
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Split('/')[0].Trim().ToUpperInvariant())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>配置里的标的以「基准/计价」字符串存储（如 BTC/USDT），在边界解析一次为值对象；只写基准币时计价币默认 USDT。</summary>
    public static SharedSymbol ParseConfiguredSymbol(string value)
    {
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? new SharedSymbol(TradingMode.PerpetualLinear, parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant())
            : new SharedSymbol(TradingMode.PerpetualLinear, value.Trim().ToUpperInvariant(), "USDT");
    }

    /// <summary>按值对象解析交易所原生合约（供公开 REST 接口使用）：USDT 永续目录 ∩ 24h 行情按基准资产匹配；目录不可用时返回 null。</summary>
    public async Task<SharedFuturesSymbol?> ResolveFuturesSymbolAsync(string exchange, SharedSymbol symbol, CancellationToken ct)
    {
        var universe = await GetUniverseAsync(exchange, ct);
        return universe
            .FirstOrDefault(u => u.Symbol.BaseAsset == symbol.BaseAsset && u.Symbol.QuoteAsset == symbol.QuoteAsset)
            ?.Symbol;
    }

    // —— 综合评分榜 ——

    private async Task<IReadOnlyList<SharedSymbol>> GetScoreAsync(string exchange, int limit, HashSet<string> excluded, CancellationToken ct)
    {
        IReadOnlyList<UniverseEntry> universe = await GetUniverseAsync(exchange, ct);
        if (excluded.Count > 0)
        {
            universe = universe.Where(u => !excluded.Contains(u.Symbol.BaseAsset)).ToList();
        }
        if (universe.Count == 0) return [];

        var count = universe.Count;
        var volumeRank = universe
            .OrderBy(u => u.Volume)
            .Select((u, index) => (u.Symbol.BaseAsset, Index: index))
            .ToDictionary(t => t.BaseAsset, t => t.Index, StringComparer.Ordinal);
        var moveRank = universe
            .OrderBy(u => u.Move)
            .Select((u, index) => (u.Symbol.BaseAsset, Index: index))
            .ToDictionary(t => t.BaseAsset, t => t.Index, StringComparer.Ordinal);
        double Percentile(int rank) => count <= 1 ? 1d : (double)rank / (count - 1);

        return universe
            .Select(u => new
            {
                u.Symbol,
                Score = 0.6 * Percentile(volumeRank[u.Symbol.BaseAsset]) + 0.4 * Percentile(moveRank[u.Symbol.BaseAsset]),
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symbol.BaseAsset, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(x => x.Symbol.SharedSymbol)
            .ToList();
    }

    // —— OI 增/减榜 ——

    private async Task<IReadOnlyList<OiMover>> GetOiRankedAsync(string exchange, int limit, bool up, HashSet<string> excluded, CancellationToken ct)
    {
        IReadOnlyList<OiMover> movers = await GetOiMoversAsync(exchange, ct);
        if (excluded.Count > 0)
        {
            movers = movers.Where(m => !excluded.Contains(m.Symbol.BaseAsset)).ToList();
        }
        var ordered = up ? movers : movers.Reverse().ToList();
        return ordered.Take(Math.Clamp(limit, 1, OiScanUniverse)).ToList();
    }

    private async Task<IReadOnlyList<OiMover>> GetOiMoversAsync(string exchange, CancellationToken ct)
    {
        if (OiCache.TryGetValue(exchange, out var cached)
            && DateTime.UtcNow - cached.At < (cached.Movers.Count > 0 ? OiCacheTtl : FailureCacheTtl))
        {
            return cached.Movers;
        }

        IReadOnlyList<OiMover> movers = [];
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(OiScanTimeout);
            movers = await FetchOiMoversAsync(exchange, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"[CoinRanking] {exchange} OI 扫描超时，回退为通用描述");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CoinRanking] {exchange} OI 扫描异常：{ex.GetType().Name} {ex.Message}");
        }

        OiCache[exchange] = (DateTime.UtcNow, movers);
        return movers;
    }

    private async Task<IReadOnlyList<OiMover>> FetchOiMoversAsync(string exchange, CancellationToken ct)
    {
        var universe = await GetUniverseAsync(exchange, ct);
        if (universe.Count == 0) return [];

        // 扫描集：成交额前 N（一视同仁，不按标的类型过滤）
        var scanSet = universe.OrderByDescending(u => u.Volume).Take(OiScanUniverse).ToList();
        var http = httpClientFactory.CreateClient();

        using var gate = new SemaphoreSlim(OiConcurrency);
        var tasks = scanSet.Select(async entry =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await FetchOiChangeAsync(http, exchange, entry.Symbol, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var movers = results.Where(r => r is not null).Select(r => r!).ToList();
        if (movers.Count == 0)
        {
            Console.WriteLine($"[CoinRanking] {exchange} OI 扫描无可用结果（扫描集 {scanSet.Count} 个标的）");
            return [];
        }

        return movers.OrderByDescending(m => m.ChangePct).ThenBy(m => m.Symbol.BaseAsset, StringComparer.Ordinal).ToList();
    }

    private static async Task<OiMover?> FetchOiChangeAsync(HttpClient http, string exchange, SharedFuturesSymbol symbol, CancellationToken ct)
    {
        // 用值对象的交易所原生合约名调用（Binance: BTCUSDT / Gate: BTC_USDT），不自行拼装
        var name = symbol.Name;
        var url = exchange == "gate"
            ? $"https://api.gateio.ws/api/v4/futures/usdt/contract_stats?contract={Uri.EscapeDataString(name)}&interval=1h&limit=2"
            : $"https://fapi.binance.com/futures/data/openInterestHist?symbol={Uri.EscapeDataString(name)}&period=1h&limit=2";
        var field = exchange == "gate" ? "open_interest" : "sumOpenInterest";

        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var items = doc.RootElement.EnumerateArray().ToList();
            if (items.Count < 2) return null;

            var first = ReadDecimal(items[0], field);
            var last = ReadDecimal(items[^1], field);
            if (first is not > 0 || last is null) return null;

            var changePct = Math.Round((last.Value - first.Value) / first.Value * 100m, 2);
            return new OiMover(symbol.SharedSymbol, changePct);
        }
        catch (OperationCanceledException)
        {
            throw; // 整体超时由外层处理
        }
        catch
        {
            return null; // 单个标的失败不影响整轮扫描
        }
    }

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    // —— 混合模式：各启用来源合并去重 ——

    private async Task<CoinSourceResolution?> ResolveMixedAsync(CoinSourceConfig cs, HashSet<string> excluded, CancellationToken ct)
    {
        var wantScore = cs.UseScore;
        var wantTop = cs.UseOiTop;
        var wantLow = cs.UseOiLow;

        // 自定义币校验：目录可用（拉取成功）时仅保留交易所确实存在的 USDT 永续标的；目录不可用时保持原样（不做误伤）
        var custom = cs.StaticCoins.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (custom.Count > 0)
        {
            var universe = await GetUniverseAsync(cs.Exchange, ct);
            if (universe.Count > 0)
            {
                var knownBases = universe.Select(u => u.Symbol.BaseAsset).ToHashSet(StringComparer.Ordinal);
                var dropped = custom.Where(c => !knownBases.Contains(ParseConfiguredSymbol(c).BaseAsset)).ToList();
                if (dropped.Count > 0)
                {
                    Console.WriteLine($"[CoinRanking] 混合模式忽略不在交易所 USDT 永续目录中的自定义币：{string.Join("、", dropped)}");
                }
                custom = custom.Where(c => knownBases.Contains(ParseConfiguredSymbol(c).BaseAsset)).ToList();
            }
        }
        if (!wantScore && !wantTop && !wantLow && custom.Count == 0) return null;

        var scoreTask = wantScore ? GetScoreAsync(cs.Exchange, cs.ScoreLimit, excluded, ct) : Task.FromResult<IReadOnlyList<SharedSymbol>>([]);
        var moversTask = wantTop || wantLow
            ? GetOiMoversAsync(cs.Exchange, ct)
            : Task.FromResult<IReadOnlyList<OiMover>>([]);
        await Task.WhenAll(scoreTask, moversTask).ConfigureAwait(false);

        var score = await scoreTask.ConfigureAwait(false);
        IReadOnlyList<OiMover> movers = await moversTask.ConfigureAwait(false);
        if (excluded.Count > 0)
        {
            movers = movers.Where(m => !excluded.Contains(m.Symbol.BaseAsset)).ToList();
        }
        var top = wantTop ? movers.Take(Math.Clamp(cs.OiTopLimit, 1, OiScanUniverse)).ToList() : [];
        var low = wantLow ? movers.Reverse().Take(Math.Clamp(cs.OiLowLimit, 1, OiScanUniverse)).ToList() : [];

        // 合并去重：各来源各按自己的 limit 贡献条目，去重前上限 = 求和
        //（对齐界面「最多 N 个币种」的 N；避免首来源填满导致后续来源永远贡献不了）
        var maxLimit = Math.Max(1, new[]
        {
            wantScore ? cs.ScoreLimit : 0,
            wantTop ? cs.OiTopLimit : 0,
            wantLow ? cs.OiLowLimit : 0,
            custom.Count,
        }.Sum());

        var merged = new List<SharedSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(SharedSymbol symbol)
        {
            if (merged.Count >= maxLimit) return;
            if (seen.Add(symbol.BaseAsset)) merged.Add(symbol);
        }

        foreach (var symbol in score) Add(symbol);
        foreach (var mover in top) Add(mover.Symbol);
        foreach (var mover in low) Add(mover.Symbol);
        foreach (var coin in custom)
        {
            var symbol = ParseConfiguredSymbol(coin);
            if (excluded.Contains(symbol.BaseAsset)) continue;
            Add(symbol);
        }

        return new CoinSourceResolution(score, top, low, merged);
    }

    // —— 全市场标的目录 + 行情（评分与 OI 扫描共用的候选全集，缓存 60s）——

    private sealed record UniverseEntry(SharedFuturesSymbol Symbol, decimal Volume, decimal Move);

    private async Task<IReadOnlyList<UniverseEntry>> GetUniverseAsync(string exchange, CancellationToken ct)
    {
        if (UniverseCache.TryGetValue(exchange, out var cached)
            && DateTime.UtcNow - cached.At < (cached.Entries.Count > 0 ? CacheTtl : FailureCacheTtl))
        {
            return cached.Entries;
        }

        IReadOnlyList<UniverseEntry> entries = [];
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FetchTimeout);
            entries = await FetchUniverseAsync(exchange, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine("[CoinRanking] 行情/目录拉取超时，回退为通用描述");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CoinRanking] 行情/目录拉取异常：{ex.GetType().Name} {ex.Message}");
        }

        // 失败结果做短缓存（20s），避免离线时反复预览/测试每次都要等满超时
        UniverseCache[exchange] = (DateTime.UtcNow, entries);
        return entries;
    }

    private static async Task<IReadOnlyList<UniverseEntry>> FetchUniverseAsync(string exchange, CancellationToken ct)
    {
        var exchangeName = exchange == "gate" ? "GateIo" : "Binance";
        var exchangeParameters = BuildExchangeParameters(exchange);

        // 1) 标的目录：值对象（BaseAsset / QuoteAsset / DeliveryTime 均为字段，无字符串拆解）
        var symbolsClient = new ExchangeRestClient();
        var symbolsResult = await symbolsClient
            .GetFuturesSymbolsAsync(exchangeName, new GetSymbolsRequest(TradingMode.PerpetualLinear, exchangeParameters: exchangeParameters), ct)
            .ConfigureAwait(false);
        if (!symbolsResult.Success || symbolsResult.Data is null)
        {
            Console.WriteLine($"[CoinRanking] {exchangeName} 标的目录拉取失败：{symbolsResult.Error}");
            return [];
        }

        // 候选口径：USDT 计价、永续（DeliveryTime 字段为空即非交割）；不按标的类型过滤
        var catalog = symbolsResult.Data
            .Where(s => s.QuoteAsset == "USDT" && s.DeliveryTime is null)
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 2) 全市场 24h 行情（公开，无需凭证）
        var configuration = new CryptoClientsConfiguration(builder => builder
            .ConfigureGlobal(options => options.RequestTimeout = TimeSpan.FromSeconds(12)));
        var tickersClient = new ExchangeSharedApiClient(configuration);
        var capability = tickersClient.GetCapability<IGetAllTickers>(exchangeName, TradingMode.PerpetualLinear);
        if (capability is null)
        {
            Console.WriteLine($"[CoinRanking] {exchangeName} 不提供全市场行情能力（PerpetualLinear），回退为通用描述");
            return [];
        }

        var tickersResult = await capability.Capability
            .GetAllTickersAsync(new GetTickersRequest(TradingMode.PerpetualLinear, exchangeParameters), ct)
            .ConfigureAwait(false);
        if (!tickersResult.Success || tickersResult.Data is null)
        {
            Console.WriteLine($"[CoinRanking] {exchangeName} 全市场行情拉取失败：{tickersResult.Error}");
            return [];
        }

        // 3) Join：行情（交易所原生符号）↔ 标的目录（值对象）；同一标的取成交额最大者
        return tickersResult.Data
            .Select(t => (Ticker: t, Symbol: catalog.GetValueOrDefault(t.Symbol)))
            .Where(x => x.Symbol is not null && x.Ticker.Volumes?.QuantityInQuoteAsset is > 0)
            .GroupBy(x => x.Symbol!.BaseAsset, StringComparer.Ordinal)
            .Select(g => g.MaxBy(x => x.Ticker.Volumes!.QuantityInQuoteAsset)!)
            .Select(x => new UniverseEntry(x.Symbol!, x.Ticker.Volumes!.QuantityInQuoteAsset!.Value, Math.Abs(x.Ticker.ChangePercentage ?? 0m)))
            .ToList();
    }

    // Gate.io 的线性永续需要显式指定结算资产（库侧强制）；Binance 无需
    private static ExchangeParameters? BuildExchangeParameters(string exchange) =>
        exchange == "gate"
            ? new ExchangeParameters([new ExchangeParameter("GateIo", "SettleAsset", "usdt")])
            : null;
}
