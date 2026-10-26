using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// 只读行情数据口（单周期缓存 <see cref="MarketDataCache"/> 的下层实现）：把现有行情/情绪/外部数据/币源服务
/// 收敛为统一的异步接口，供行情工具与后续 Agent 阶段复用；不改变底层服务行为。
/// </summary>
public interface IMarketDataReader
{
    /// <summary>K 线快照（display = bars 根展示，warmupBars = 指标计算所需深度；失败时 Source = missing）。</summary>
    Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
        int bars, int warmupBars, CancellationToken ct);

    /// <summary>BTC/USDT 大盘概览（失败返回 null）。</summary>
    Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct);

    /// <summary>逐标的持仓量/资金费率情绪（以基准资产为键）。</summary>
    Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
        IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct);

    /// <summary>内置新闻源一轮拉取结果。</summary>
    Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(CancellationToken ct);

    /// <summary>币种候选解析（静态/网格来源返回 null）。</summary>
    Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct);
}
