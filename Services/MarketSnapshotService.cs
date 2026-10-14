using CryptoClients.Net;
using CryptoClients.Net.Clients;
using CryptoClients.Net.Models;
using CryptoExchange.Net.SharedApis;
using Polly;
using Polly.Retry;

namespace LuckyCat.Services;

public record MarketKline(DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>行情快照：Source = live（实时）或 missing（拉取失败，如实标注缺失、绝不以合成数据顶替）。
/// 24h 三件套（涨跌幅/高低/成交额）与最新价同源（同一次 ticker 请求）；为滚动窗口口径（基数效应会产生非异动的数字翻转，提示词「行情判读」已声明）。</summary>
public record MarketSnapshot(
    string Source,
    string Symbol,
    decimal? LastPrice,
    IReadOnlyList<MarketKline> Klines,
    decimal? ChangePct24h = null,
    decimal? High24h = null,
    decimal? Low24h = null,
    decimal? QuoteVolume24h = null,
    string? Failure = null,
    DateTime? AsOfUtc = null,
    bool TickerAvailable = true);

public class MarketSnapshotService
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);
    // 突发超时重试（Polly 管道）：最多 3 次、线性退避 1/2/3 秒——链路抖动多为一过性，重试即可恢复；仍失败才标注缺失
    private const int MaxRetries = 3;
    private static readonly ResiliencePipeline<MarketSnapshot?> FetchPipeline = new ResiliencePipelineBuilder<MarketSnapshot?>()
        .AddRetry(new RetryStrategyOptions<MarketSnapshot?>
        {
            MaxRetryAttempts = MaxRetries,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Linear,
            UseJitter = false,
            ShouldHandle = new PredicateBuilder<MarketSnapshot?>()
                .Handle<Exception>(ex => ex is not OperationCanceledException)
                .HandleResult(snapshot => snapshot is null),
            OnRetry = args =>
            {
                var reason = args.Outcome.Exception?.GetType().Name ?? "返回空";
                Console.WriteLine($"[MarketSnapshot] 第 {args.AttemptNumber + 1} 次尝试失败（{reason}），重试");
                return ValueTask.CompletedTask;
            },
        })
        .Build();

    /// <summary>公开行情拉取（无需凭证）；失败时先重试最多 3 次，仍失败返回 missing 快照（带失败原因）——
    /// 如实标注数据缺失，绝不以合成数据顶替。标的以值对象（SharedSymbol）传入；bars 为 K 线根数。
    /// computeBars 为服务端指标计算所需的深度（大于 bars 时多取历史行、不减展示行数）。</summary>
    public virtual async Task<MarketSnapshot> GetAsync(string exchange, SharedSymbol symbol, string timeframe, int bars, CancellationToken ct, int computeBars = 0)
    {
        var display = $"{symbol.BaseAsset}/{symbol.QuoteAsset}";
        var fetchBars = Math.Clamp(Math.Max(bars, computeBars), 1, 300);
        var (live, error) = await FetchWithRetryAsync(exchange, symbol, timeframe, fetchBars, ct).ConfigureAwait(false);
        if (live is not null) return live;

        Console.WriteLine($"[MarketSnapshot] {exchange} {display} {timeframe} 重试 {MaxRetries} 次仍失败（{error}），标注数据缺失");
        Telemetry.ExchangeFailures.Add(1,
            new KeyValuePair<string, object?>("exchange", exchange),
            new KeyValuePair<string, object?>("symbol", display));
        return new MarketSnapshot("missing", display, null, [], Failure: error);
    }

    // 单序列拉取（Polly 管道重试 3 次；单次尝试沿用 20 秒超时；外层取消如实上抛）
    private static async Task<(MarketSnapshot? Snapshot, string? Error)> FetchWithRetryAsync(
        string exchange, SharedSymbol symbol, string timeframe, int bars, CancellationToken ct)
    {
        string? lastError = null;
        var attempts = 0;
        try
        {
            var snapshot = await FetchPipeline.ExecuteAsync(async token =>
            {
                attempts++;
                using var fetchActivity = Telemetry.Source.StartActivity("exchange.fetch");
                fetchActivity?.SetTag("exchange", exchange);
                fetchActivity?.SetTag("symbol", $"{symbol.BaseAsset}/{symbol.QuoteAsset}");
                fetchActivity?.SetTag("timeframe", timeframe);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(FetchTimeout);
                    var live = await TryFetchAsync(exchange, symbol, timeframe, bars, cts.Token).ConfigureAwait(false);
                    lastError = live is null ? "请求失败" : null;
                    fetchActivity?.SetTag("exchange.result", live is null ? "empty" : "ok");
                    return live;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    lastError = "拉取超时";
                    fetchActivity?.SetTag("exchange.result", "timeout");
                    return null;
                }
            }, ct).ConfigureAwait(false);

            if (attempts > 1)
            {
                Telemetry.ExchangeRetries.Add(attempts - 1,
                    new KeyValuePair<string, object?>("exchange", exchange),
                    new KeyValuePair<string, object?>("symbol", $"{symbol.BaseAsset}/{symbol.QuoteAsset}"));
            }

            return (snapshot, snapshot is null ? lastError ?? "请求失败" : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"拉取异常（{ex.GetType().Name}）");
        }
    }

    private static async Task<MarketSnapshot?> TryFetchAsync(string exchange, SharedSymbol symbol, string timeframe, int bars, CancellationToken ct)
    {
        var exchangeName = exchange == "gate" ? "GateIo" : "Binance";
        if (!TryMapInterval(timeframe, out var interval)) return null;

        // 公开行情无需凭证（与 ExchangeVerificationRunner 同构，但不配置 ApiCredentials）；
        // 口径为 USDT 永续，与「币源交易所」的语义及综合评分榜的候选池一致
        var configuration = new CryptoClientsConfiguration(builder => builder
            .ConfigureGlobal(options =>
            {
                options.RequestTimeout = TimeSpan.FromSeconds(15);
            }));
        var client = new ExchangeSharedApiClient(configuration);
        var exchangeParameters = BuildExchangeParameters(exchange);

        // 最新价与 24h 三件套同源（同一次 ticker 请求；失败时如实置空）
        SharedTicker? tickerData = null;
        var ticker = client.GetCapability<IGetTicker>(exchangeName, TradingMode.PerpetualLinear);
        if (ticker is not null)
        {
            var tickerResult = await ticker.Capability
                .GetTickerAsync(new GetTickerRequest(symbol, exchangeParameters), ct).ConfigureAwait(false);
            if (tickerResult.Success) tickerData = tickerResult.Data;
        }

        var lastPrice = tickerData?.LastPrice;
        var klines = client.GetCapability<IGetKlines>(exchangeName, TradingMode.PerpetualLinear);
        if (klines is null)
        {
            Console.WriteLine($"[MarketSnapshot] {exchangeName} 不支持 K 线能力，标注数据缺失");
            return null;
        }

        var klineResult = await klines.Capability
            .GetKlinesAsync(new GetKlinesRequest(symbol, interval, limit: Math.Clamp(bars, 1, 300), exchangeParameters: exchangeParameters), null, ct)
            .ConfigureAwait(false);
        if (!klineResult.Success || klineResult.Data is null || klineResult.Data.Length == 0)
        {
            // 如实记录底层失败原因（超时/限流/空数据），供诊断掐流形态用
            Console.WriteLine($"[MarketSnapshot] {exchangeName} {symbol.BaseAsset}/{symbol.QuoteAsset} {timeframe} K 线请求失败：{klineResult.Error?.ToString() ?? "空数据"}");
            return null;
        }

        var rows = klineResult.Data
            .Select(k => new MarketKline(k.OpenTime, k.OpenPrice, k.HighPrice, k.LowPrice, k.ClosePrice, k.Volumes?.QuantityInBaseAsset ?? 0m))
            .OrderBy(k => k.OpenTime)
            .ToList();
        return new MarketSnapshot("live", $"{symbol.BaseAsset}/{symbol.QuoteAsset}", lastPrice ?? rows[^1].Close, rows,
            tickerData?.ChangePercentage, tickerData?.HighPrice, tickerData?.LowPrice, tickerData?.Volumes?.QuantityInQuoteAsset,
            AsOfUtc: DateTime.UtcNow, TickerAvailable: tickerData is not null);
    }

    /// <summary>大盘概览（BTC/USDT 最新价 + 24h 三件套）：供行情快照顶部「大盘」行注入；失败（含重试）如实返回 null。</summary>
    public virtual async Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct)
    {
        var btc = new SharedSymbol(TradingMode.PerpetualLinear, "BTC", "USDT");
        var (live, _) = await FetchWithRetryAsync(exchange, btc, "1h", 1, ct).ConfigureAwait(false);
        return live;
    }

    // Gate.io 的线性永续需要显式指定结算资产（库侧强制）；Binance 无需
    private static ExchangeParameters? BuildExchangeParameters(string exchange) =>
        exchange == "gate"
            ? new ExchangeParameters([new ExchangeParameter("GateIo", "SettleAsset", "usdt")])
            : null;

    private static bool TryMapInterval(string timeframe, out SharedKlineInterval interval)
    {
        interval = timeframe switch
        {
            "1m" => SharedKlineInterval.OneMinute,
            "3m" => SharedKlineInterval.ThreeMinutes,
            "5m" => SharedKlineInterval.FiveMinutes,
            "15m" => SharedKlineInterval.FifteenMinutes,
            "30m" => SharedKlineInterval.ThirtyMinutes,
            "1h" => SharedKlineInterval.OneHour,
            "2h" => SharedKlineInterval.TwoHours,
            "4h" => SharedKlineInterval.FourHours,
            "6h" => SharedKlineInterval.SixHours,
            "8h" => SharedKlineInterval.EightHours,
            "12h" => SharedKlineInterval.TwelveHours,
            "1D" or "1d" => SharedKlineInterval.OneDay,
            "1W" or "1w" => SharedKlineInterval.OneWeek,
            _ => (SharedKlineInterval)(-1),
        };
        return (int)interval >= 0;
    }

}
