using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.SharedApis;
using CryptoClients.Net;
using CryptoClients.Net.Clients;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using LuckyCat.Data;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>单账户的实盘能力审计结果：硬性要求（下标单/持仓/余额/止损支持）不满足时拒绝实盘启动。</summary>
public record TradingAudit(
    bool CanTrade,
    bool StopLossSupported,
    bool TakeProfitSupported,
    bool LeverageSupported,
    bool CloseSupported,
    bool PositionHistorySupported,
    IReadOnlyList<string> Notes);

/// <summary>账户快照（净值/可用，USDT）。</summary>
public record AccountSnapshot(bool Success, decimal UsdtEquity, decimal UsdtAvailable, string? Error);
public record PositionReadResult(bool Success, IReadOnlyList<SymbolPosition> Positions, string? Error);

/// <summary>单标的持仓（看板用）。</summary>
public record SymbolPosition(string Symbol, SharedPositionSide Side, decimal Quantity, decimal AverageOpenPrice, decimal? UnrealizedPnl, decimal? LiquidationPrice, decimal? Leverage);

/// <summary>持仓历史条目（共享接口直接给出；Binance 回退为成交重建时结构/时间/盈亏为真实值，明细视来源而定）。</summary>
public record PositionHistoryEntry(
    string Symbol,
    decimal RealizedPnl,
    decimal? Fee,
    DateTime Time,
    string? Side,
    decimal? EntryPrice,
    decimal? ExitPrice,
    decimal? Quantity,
    decimal? Leverage,
    string Source,
    DateTime? OpenTime = null);

/// <summary>持仓历史读取结果：条目 + 费用口径（逐行费用或仅合计）+ 数据来源说明。</summary>
public record PositionHistoryResult(IReadOnlyList<PositionHistoryEntry> Entries, decimal AggregateFee, bool FeeOnlyAggregate, string? Note, string? Error = null);

/// <summary>
/// 实盘交易服务（USDT 永续）：按官方 Shared API V2 指引使用 GetCapability 解析能力，
/// 依据 Options.RequestParameterRules 判断交易所是否真的接受止损/止盈参数（不凭请求模型推断）；客户端按账户复用。
/// 全部方法如实返回错误，不吞异常。
/// </summary>
public class ExchangeTradingService
{
    private static readonly TimeSpan AccountCacheTtl = TimeSpan.FromSeconds(10);
    // 仓位历史读取缓存：与 5 秒同步节奏匹配（保证每轮同步都取到新数据）
    private static readonly TimeSpan HistoryCacheTtl = TimeSpan.FromSeconds(4);

    private static readonly ConcurrentDictionary<int, (DateTime At, AccountSnapshot Value)> AccountCache = new();
    private static readonly ConcurrentDictionary<int, (DateTime At, IReadOnlyList<SymbolPosition> Value)> PositionCache = new();
    private static readonly ConcurrentDictionary<int, (DateTime At, PositionHistoryResult Value)> HistoryCache = new();

    /// <summary>能力审计：用同一套口径回答「该账户能不能安全地自动下单」。</summary>
    public static TradingAudit Audit(ExchangeAccount account)
    {
        var notes = new List<string>();
        ExchangeSharedApiClient client;
        try
        {
            client = ExchangeClientFactory.Get(account);
        }
        catch (Exception ex)
        {
            return new TradingAudit(false, false, false, false, false, false, [$"客户端创建失败：{ex.Message}"]);
        }

        var name = ExchangeClientFactory.Name(account.ExchangeType);
        var parameters = ExchangeClientFactory.Parameters(account);

        var order = client.GetCapability<IPlaceFuturesOrder>(name, TradingMode.PerpetualLinear);
        var positions = client.GetCapability<IGetPositions>(name, TradingMode.PerpetualLinear);
        var balances = client.GetCapability<IGetBalances>(name, TradingMode.PerpetualLinear);
        var leverage = client.GetCapability<ISetLeverage>(name, TradingMode.PerpetualLinear);
        var close = client.GetCapability<ICloseFullPosition>(name, TradingMode.PerpetualLinear);
        var history = client.GetCapability<IGetPositionHistory>(name, TradingMode.PerpetualLinear);

        if (order is null) notes.Add("不支持 USDT 永续下单（IPlaceFuturesOrder）");
        if (positions is null) notes.Add("不支持持仓查询（IGetPositions）");
        if (balances is null) notes.Add("不支持余额查询（IGetBalances）");
        if (leverage is null) notes.Add("不支持杠杆设置（ISetLeverage），将沿用账户当前杠杆");
        if (close is null) notes.Add("无整仓平仓能力（ICloseFullPosition 不可用），平仓将走反向市价单兜底（按持仓模式自适应）");
        if (history is null)
        {
            notes.Add(account.ExchangeType == ExchangeType.Binance
                ? "共享接口不支持持仓历史，已回退为交易所原生收入流水（REALIZED_PNL + 费用合计）"
                : "不支持持仓历史（IGetPositionHistory），看板历史统计将为空");
        }

        // 参数规则：文档明确「不要凭请求模型推断支持」——止损为硬性要求（无保护不开仓）
        var slSupported = IsParameterSupported(order, nameof(PlaceFuturesOrderRequest.StopLossPrice));
        var tpSupported = IsParameterSupported(order, nameof(PlaceFuturesOrderRequest.TakeProfitPrice));
        if (!slSupported) notes.Add("下单接口不接受止损参数（StopLossPrice 不满足 Required/Optional），无法附加保护，拒绝自动开仓");
        if (!tpSupported) notes.Add("下单接口不接受止盈参数（TakeProfitPrice），止盈将由策略轮次信号代替");

        var canTrade = order is not null && positions is not null && balances is not null && slSupported;
        _ = parameters; // 供后续扩展：部分交易所需要位置模式等参数
        return new TradingAudit(
            canTrade,
            slSupported,
            tpSupported,
            leverage is not null,
            close is not null,
            history is not null,
            notes);
    }

    private static bool IsParameterSupported<T>(SharedCapabilityResolution<T>? match, string parameterName) where T : class, ISharedApiCapability
    {
        if (match?.Options is null) return false;
        // 官方口径：RequestParameterRules 逐字段给出 Required/Optional/NotSupported；ToString 保留枚举名便于日志
        var rule = match.Options.RequestParameterRules?.FirstOrDefault(r => r.Name == parameterName);
        if (rule is null) return false;
        var text = rule.ToString() ?? "";
        return text.Contains("Required", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Optional", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>账户 USDT 净值/可用（10 秒缓存，抵御看板轮询）。</summary>
    public async Task<AccountSnapshot> GetAccountAsync(ExchangeAccount account, CancellationToken ct)
    {
        if (AccountCache.TryGetValue(account.Id, out var cached) && DateTime.UtcNow - cached.At < AccountCacheTtl)
        {
            return cached.Value;
        }

        var value = await FetchAccountAsync(account, ct).ConfigureAwait(false);
        AccountCache[account.Id] = (DateTime.UtcNow, value);
        return value;
    }

    private static async Task<AccountSnapshot> FetchAccountAsync(ExchangeAccount account, CancellationToken ct)
    {
        try
        {
            var client = ExchangeClientFactory.Get(account);
            var capability = client.GetCapability<IGetBalances>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.PerpetualLinear)
                ?? client.GetCapability<IGetBalances>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.Spot);
            if (capability is null) return new AccountSnapshot(false, 0, 0, "该交易所不支持余额查询");

            var result = await capability.Capability
                .GetBalancesAsync(new GetBalancesRequest(TradingMode.PerpetualLinear), ct)
                .ConfigureAwait(false);
            if (!result.Success || result.Data is null)
            {
                return new AccountSnapshot(false, 0, 0, result.Error?.ToString() ?? "余额查询失败");
            }

            var usdt = result.Data.FirstOrDefault(b => string.Equals(b.Asset, "USDT", StringComparison.OrdinalIgnoreCase));
            if (usdt is null) return new AccountSnapshot(false, 0, 0, "账户无 USDT 余额记录");

            var available = Math.Max(0m, usdt.Available);
            return new AccountSnapshot(true, usdt.Total, available, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AccountSnapshot(false, 0, 0, "余额查询超时");
        }
        catch (Exception ex)
        {
            return new AccountSnapshot(false, 0, 0, ex.Message);
        }
    }

    // —— Binance 杠杆映射（positionRisk 全量；含已平仓标的的"当前杠杆设置"，与官网仓位历史口径一致）——

    private static readonly ConcurrentDictionary<int, (DateTime At, IReadOnlyDictionary<string, decimal> Value)> LeverageMapCache = new();

    /// <summary>拉取各标的当前杠杆设置（先全量 positionRisk，失败/为空则退化到逐标查询）。</summary>
    public async Task<IReadOnlyDictionary<string, decimal>> GetBinanceLeverageMapAsync(ExchangeAccount account, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        if (account.ExchangeType != ExchangeType.Binance) return new Dictionary<string, decimal>();
        if (LeverageMapCache.TryGetValue(account.Id, out var cached)
            && DateTime.UtcNow - cached.At < (cached.Value.Count > 0 ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30)))
        {
            return cached.Value;
        }

        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        try
        {
            var client = GetBinanceClient(account);
            var result = await client.UsdFuturesApi.Account
                .GetPositionInformationAsync(null, null, ct)
                .ConfigureAwait(false);
            if (result.Success && result.Data is not null)
            {
                foreach (var p in result.Data)
                {
                    if (!string.IsNullOrWhiteSpace(p.Symbol) && p.Leverage > 0)
                    {
                        map[p.Symbol!] = p.Leverage;
                    }
                }
            }
            else if (!result.Success)
            {
                Console.WriteLine($"[Trading] Binance 杠杆设置全量拉取失败：{result.Error}，尝试逐标查询");
            }

            // 全量为空（或失败）时逐标查询（限 15 个）
            if (map.Count == 0)
            {
                foreach (var symbol in symbols.Distinct(StringComparer.Ordinal).Take(15))
                {
                    var single = await client.UsdFuturesApi.Account
                        .GetPositionInformationAsync(symbol, null, ct)
                        .ConfigureAwait(false);
                    if (single.Success && single.Data is not null)
                    {
                        foreach (var p in single.Data)
                        {
                            if (!string.IsNullOrWhiteSpace(p.Symbol) && p.Leverage > 0)
                            {
                                map[p.Symbol!] = p.Leverage;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trading] Binance 杠杆设置拉取异常：{ex.GetType().Name} {ex.Message}");
        }

        LeverageMapCache[account.Id] = (DateTime.UtcNow, map);
        return map;
    }

    /// <summary>当前持仓（10 秒缓存；平仓等资金操作请用新鲜读取）。</summary>
    public async Task<IReadOnlyList<SymbolPosition>> GetPositionsAsync(ExchangeAccount account, CancellationToken ct)
    {
        if (PositionCache.TryGetValue(account.Id, out var cached) && DateTime.UtcNow - cached.At < AccountCacheTtl)
        {
            return cached.Value;
        }

        var value = await FetchPositionsAsync(account, ct).ConfigureAwait(false);
        PositionCache[account.Id] = (DateTime.UtcNow, value);
        return value;
    }

    // 风控读取不使用展示缓存；读取失败不得伪装成“无持仓”。
    public Task<PositionReadResult> GetPositionSnapshotAsync(ExchangeAccount account, CancellationToken ct) =>
        FetchPositionSnapshotAsync(account, ct);

    public Task<AccountSnapshot> GetFreshAccountAsync(ExchangeAccount account, CancellationToken ct) =>
        FetchAccountAsync(account, ct);

    /// <summary>
    /// 持仓采样落库（供历史仓位「杠杆」回填）：仅当与最近一次采样不同（数量/方向/杠杆变化）时新增一行，控制体积。
    /// 采样点：交易员每轮运行、打开看板、手动平仓前；进程内串行化避免并发重复。
    /// </summary>
    private static readonly SemaphoreSlim SnapshotLock = new(1, 1);

    public async Task CaptureSnapshotsAsync(AppDbContext db, ExchangeAccount account, CancellationToken ct)
    {
        await SnapshotLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var positions = await GetPositionsAsync(account, ct).ConfigureAwait(false);
            if (positions.Count == 0) return;

            var recent = await db.AccountPositionSnapshots
                .Where(s => s.ExchangeAccountId == account.Id)
                .OrderByDescending(s => s.Id)
                .Take(200)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var latest = recent
                .GroupBy(s => s.Symbol, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var now = DateTime.UtcNow;
            var added = false;
            foreach (var position in positions)
            {
                var side = position.Side.ToString();
                if (latest.TryGetValue(position.Symbol, out var last)
                    && last.Leverage == position.Leverage
                    && last.Quantity == position.Quantity
                    && last.Side == side)
                {
                    continue;
                }

                db.AccountPositionSnapshots.Add(new AccountPositionSnapshot
                {
                    ExchangeAccountId = account.Id,
                    At = now,
                    Symbol = position.Symbol,
                    Side = side,
                    Quantity = position.Quantity,
                    EntryPrice = position.AverageOpenPrice,
                    Leverage = position.Leverage,
                });
                added = true;
            }

            if (added)
            {
                await db.SaveChangesAsync(ct);
                Console.WriteLine($"[Trading] 持仓采样已更新（账户 {account.Id}，当前 {positions.Count} 个持仓）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trading] 持仓采样落库失败：{ex.Message}");
        }
        finally
        {
            SnapshotLock.Release();
        }
    }

    // 持仓新鲜读取（不走缓存）
    private static async Task<IReadOnlyList<SymbolPosition>> FetchPositionsAsync(ExchangeAccount account, CancellationToken ct) =>
        (await FetchPositionSnapshotAsync(account, ct).ConfigureAwait(false)).Positions;

    private static async Task<PositionReadResult> FetchPositionSnapshotAsync(ExchangeAccount account, CancellationToken ct)
    {
        try
        {
            var client = ExchangeClientFactory.Get(account);
            var capability = client.GetCapability<IGetPositions>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.PerpetualLinear);
            if (capability is not null)
            {
                var result = await capability.Capability
                    .GetPositionsAsync(new GetPositionsRequest(TradingMode.PerpetualLinear, ExchangeClientFactory.Parameters(account)), ct)
                    .ConfigureAwait(false);
                if (result.Success && result.Data is not null)
                {
                    return new PositionReadResult(true, result.Data
                        .Select(p =>
                        {
                            var quantity = p.PositionSizes?.QuantityInBaseAsset ?? p.PositionSizes?.QuantityInContracts ?? 0m;
                            return (Position: p, Quantity: quantity);
                        })
                        .Where(x => x.Quantity != 0)
                        .Select(x => new SymbolPosition(
                            x.Position.Symbol ?? "", x.Position.PositionSide, x.Quantity, x.Position.AverageOpenPrice ?? 0m,
                            x.Position.UnrealizedPnl, x.Position.LiquidationPrice, x.Position.Leverage))
                        .ToList(), null);
                }

                return new(false, [], result.Error?.ToString() ?? "持仓查询失败");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine("[Trading] 持仓查询超时");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trading] 持仓查询异常：{ex.GetType().Name} {ex.Message}");
        }

        return new(false, [], "持仓查询失败或不支持");
    }

    /// <summary>
    /// 持仓历史（60 秒缓存）：①共享接口 IGetPositionHistory（可用交易所，逐笔含入场/出场价等明细）；
    /// ②回退：Binance 等共享层未实现的交易所，改用**原生收入流水**（REALIZED_PNL 逐条 + COMMISSION/FUNDING_FEE 合计费用）。
    /// </summary>
    public async Task<PositionHistoryResult> GetPositionHistoryAsync(ExchangeAccount account, int limit, CancellationToken ct)
    {
        if (HistoryCache.TryGetValue(account.Id, out var cached) && DateTime.UtcNow - cached.At < HistoryCacheTtl)
        {
            return cached.Value;
        }

        var value = await FetchPositionHistoryAsync(account, limit, ct).ConfigureAwait(false);
        HistoryCache[account.Id] = (DateTime.UtcNow, value);
        return value;
    }

    private static async Task<PositionHistoryResult> FetchPositionHistoryAsync(ExchangeAccount account, int limit, CancellationToken ct)
    {
        string? lastError = null;
        // ① 共享接口
        try
        {
            var client = ExchangeClientFactory.Get(account);
            var capability = client.GetCapability<IGetPositionHistory>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.PerpetualLinear);
            if (capability is not null)
            {
                var result = await capability.Capability
                    .GetPositionHistoryAsync(
                        new GetPositionHistoryRequest(TradingMode.PerpetualLinear, null, null, Math.Clamp(limit, 1, 200), DataDirection.Descending, ExchangeClientFactory.Parameters(account)),
                        null,
                        ct)
                    .ConfigureAwait(false);
                if (result.Success && result.Data is not null)
                {
                    var entries = result.Data
                        .Select(h => new PositionHistoryEntry(
                            h.Symbol ?? "",
                            h.RealizedPnl,
                            null,
                            h.Timestamp,
                            h.PositionSide.ToString(),
                            h.AverageOpenPrice,
                            h.AverageClosePrice,
                            h.Quantities?.QuantityInBaseAsset,
                            h.Leverage,
                            "shared"))
                        .ToList();
                    return new PositionHistoryResult(entries, 0m, false, null);
                }

                if (!result.Success)
                {
                    lastError = $"持仓历史查询失败：{result.Error}";
                    Console.WriteLine($"[Trading] {lastError}");
                }
            }
        }
        catch (Exception ex)
        {
            lastError = $"持仓历史查询异常（{ex.GetType().Name}）：{ex.Message}";
            Console.WriteLine($"[Trading] {lastError}");
        }

        // ② 回退：Binance 原生数据（成交明细重建 → 失败再退收入流水）
        if (account.ExchangeType == ExchangeType.Binance)
        {
            var fallback = await FetchBinancePositionHistoryAsync(account, limit, ct).ConfigureAwait(false);
            return fallback.Error is null && fallback.Entries.Count == 0 && lastError is not null
                ? fallback with { Error = lastError }
                : fallback;
        }

        return new PositionHistoryResult([], 0m, false, null, lastError);
    }

    // —— Binance 回退①：成交明细重建（字段最全）；失败再退收入流水 ——

    /// <summary>
    /// 按标的拉原生成交明细（上限 1000 条/标的）并做 FIFO 开平仓重建：
    /// 方向/数量/入场均价/平仓均价/已实现盈亏/逐笔手续费/平仓时间均为真实值；
    /// 杠杆字段原生成交不含（显示 —）；未平仓位不计入。
    /// </summary>
    private static async Task<PositionHistoryResult> FetchBinancePositionHistoryAsync(ExchangeAccount account, int limit, CancellationToken ct)
    {
        try
        {
            var client = GetBinanceClient(account);

            // 1) 收入流水发现交易过的标的（按最近结算取前 10 个）
            var income = await client.UsdFuturesApi.Account
                .GetIncomeHistoryAsync(null, null, null, null, null, Math.Clamp(limit, 1, 1000), null, ct)
                .ConfigureAwait(false);
            if (!income.Success || income.Data is null)
            {
                var error = $"Binance 收入流水查询失败（重建前置）：{income.Error}";
                Console.WriteLine($"[Trading] {error}");
                return new PositionHistoryResult([], 0m, false, null, Error: error);
            }

            var records = income.Data.ToList();
            var settlementSymbols = records
                .Where(r => r.IncomeType == IncomeType.RealizedPnl && !string.IsNullOrWhiteSpace(r.Symbol))
                .Select(r => r.Symbol!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var symbols = records
                .Where(r => r.IncomeType == IncomeType.RealizedPnl && !string.IsNullOrWhiteSpace(r.Symbol))
                .OrderByDescending(r => r.Timestamp)
                .Select(r => r.Symbol!)
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToList();

            // 2) 逐标的拉成交明细并重建
            var positions = new List<PositionHistoryEntry>();
            var nonUsdtFee = false;
            foreach (var symbol in symbols)
            {
                var trades = await client.UsdFuturesApi.Trading
                    .GetUserTradesAsync(symbol, null, null, 1000, null, null, null, ct)
                    .ConfigureAwait(false);
                if (!trades.Success || trades.Data is null)
                {
                    Console.WriteLine($"[Trading] {symbol} 成交明细查询失败：{trades.Error}");
                    continue;
                }

                var (rows, hasNonUsdtFee) = ReconstructPositions(symbol, trades.Data);
                positions.AddRange(rows);
                nonUsdtFee = nonUsdtFee || hasNonUsdtFee;
            }

            if (positions.Count == 0)
            {
                // 重建无结果：退回收入流水口径
                return await FetchBinanceIncomeHistoryAsync(account, limit, ct).ConfigureAwait(false);
            }

            var entries = positions
                .OrderByDescending(p => p.Time)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToList();

            var note = $"数据来源：原生成交明细重建（FIFO 匹配开平仓，覆盖最近 {symbols.Count} 个交易标的）"
                + (settlementSymbols.Count > symbols.Count ? "；更早标的未纳入" : "")
                + "；杠杆字段原生成交不含（优先持仓采样回填，其次取该标的当前杠杆设置）；未平仓位不计入"
                + (nonUsdtFee ? "；非 USDT 计价手续费（如 BNB 抵扣）未计入" : "");
            return new PositionHistoryResult(entries, 0m, false, note);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine("[Trading] Binance 成交明细重建超时");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trading] Binance 成交明细重建异常：{ex.GetType().Name} {ex.Message}");
        }

        return await FetchBinanceIncomeHistoryAsync(account, limit, ct).ConfigureAwait(false);
    }

    // FIFO 仓位重建：双向持仓按 PositionSide 判开平；单向持仓按净仓位方向推断（含反手拆分）
    private static (List<PositionHistoryEntry> Rows, bool HasNonUsdtFee) ReconstructPositions(string symbol, IEnumerable<BinanceFuturesTrade> fills)
    {
        var rows = new List<PositionHistoryEntry>();
        var hasNonUsdtFee = false;

        bool? isLong = null;
        var lots = new List<(decimal Qty, decimal Price, DateTime Time)>();
        decimal entryNotional = 0m, exitNotional = 0m, closedQty = 0m, realized = 0m, fee = 0m;
        DateTime? firstOpenTime = null;

        void FinalizePosition(DateTime closeTime)
        {
            if (closedQty <= 0) return;
            rows.Add(new PositionHistoryEntry(
                symbol,
                Math.Round(realized, 8),
                Math.Round(fee, 8),
                closeTime,
                isLong == true ? "Long" : "Short",
                Math.Round(entryNotional / closedQty, 10),
                Math.Round(exitNotional / closedQty, 10),
                closedQty,
                null,
                "binance-trades",
                firstOpenTime));
            isLong = null;
            lots.Clear();
            entryNotional = exitNotional = closedQty = realized = fee = 0m;
            firstOpenTime = null;
        }

        foreach (var fill in fills.OrderBy(f => f.Timestamp))
        {
            var buy = fill.Side == OrderSide.Buy;
            bool isOpen;
            bool fillLong;
            if (fill.PositionSide == PositionSide.Long) { isOpen = buy; fillLong = true; }
            else if (fill.PositionSide == PositionSide.Short) { isOpen = !buy; fillLong = false; }
            else { fillLong = buy; isOpen = isLong is null || isLong == fillLong; }

            var feeShare = fill.FeeAsset == "USDT" ? fill.Fee : 0m;
            if (fill.FeeAsset != "USDT" && fill.Fee > 0) hasNonUsdtFee = true;

            if (isOpen)
            {
                isLong ??= fillLong;
                firstOpenTime ??= fill.Timestamp;
                lots.Add((fill.Quantity, fill.Price, fill.Timestamp));
                entryNotional += fill.Quantity * fill.Price;
                fee += feeShare;
                continue;
            }

            // 平仓：FIFO 消耗批次（手续费按成交内占比分摊）
            var remaining = fill.Quantity;
            while (remaining > 0 && lots.Count > 0)
            {
                var lot = lots[0];
                var matched = Math.Min(lot.Qty, remaining);
                exitNotional += matched * fill.Price;
                closedQty += matched;
                fee += feeShare * (matched / fill.Quantity);
                remaining -= matched;
                if (matched == lot.Qty) lots.RemoveAt(0);
                else lots[0] = (lot.Qty - matched, lot.Price, lot.Time);
            }

            // 该成交的已实现盈亏归属当前被平仓位
            realized += fill.RealizedPnl;

            if (lots.Count == 0)
            {
                FinalizePosition(fill.Timestamp);
                // 反手：成交剩余数量作为新仓位开仓（单向模式可能出现）
                if (remaining > 0)
                {
                    isLong = !fillLong;
                    lots.Add((remaining, fill.Price, fill.Timestamp));
                    entryNotional += remaining * fill.Price;
                }
            }
        }

        return (rows, hasNonUsdtFee);
    }
    // Binance 原生客户端（按账户缓存；仅回退路径使用）
    private static readonly ConcurrentDictionary<string, BinanceRestClient> BinanceClientCache = new();

    private static BinanceRestClient GetBinanceClient(ExchangeAccount account)
    {
        var key = $"{account.Id}:{account.UpdatedAt.Ticks}";
        return BinanceClientCache.GetOrAdd(key, _ => new BinanceRestClient(options =>
        {
            options.ApiCredentials = new BinanceCredentials(account.ApiKey, account.ApiSecret);
            options.RequestTimeout = TimeSpan.FromSeconds(20);
        }));
    }

    // 收入流水：REALIZED_PNL 逐条成行；COMMISSION 与 FUNDING_FEE 取绝对值合计为交易成本（无法逐行配对，页面注明）
    private static async Task<PositionHistoryResult> FetchBinanceIncomeHistoryAsync(ExchangeAccount account, int limit, CancellationToken ct)
    {
        try
        {
            var client = GetBinanceClient(account);
            var result = await client.UsdFuturesApi.Account
                .GetIncomeHistoryAsync(null, null, null, null, null, Math.Clamp(limit, 1, 1000), null, ct)
                .ConfigureAwait(false);
            if (!result.Success || result.Data is null)
            {
                Console.WriteLine($"[Trading] Binance 收入流水查询失败：{result.Error}");
                return new PositionHistoryResult([], 0m, false, null);
            }

            var records = result.Data.ToList();
            var entries = records
                .Where(r => r.IncomeType == Binance.Net.Enums.IncomeType.RealizedPnl)
                .Select(r => new PositionHistoryEntry(
                    r.Symbol ?? "",
                    r.Income,
                    null,
                    r.Timestamp,
                    null, null, null, null, null,
                    "binance-income"))
                .OrderByDescending(e => e.Time)
                .ToList();

            var aggregateFee = records
                .Where(r => r.IncomeType == Binance.Net.Enums.IncomeType.Commission
                    || r.IncomeType == Binance.Net.Enums.IncomeType.FundingFee)
                .Sum(r => Math.Abs(r.Income));

            var note = $"数据来源：交易所原生收入流水（平仓结算 {entries.Count} 条；方向/价格/数量等明细字段不可得；手续费/资金费合计 {aggregateFee:0.####} USDT，无法逐笔配对故仅列合计）";
            return new PositionHistoryResult(entries, Math.Round(aggregateFee, 4), true, note);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine("[Trading] Binance 收入流水查询超时");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trading] Binance 收入流水查询异常：{ex.GetType().Name} {ex.Message}");
        }

        return new PositionHistoryResult([], 0m, false, null);
    }

    /// <summary>最新成交价（下单定仓用；PerpetualLinear）。</summary>
    public static async Task<decimal?> GetPriceAsync(ExchangeAccount account, SharedSymbol symbol, CancellationToken ct)
    {
        try
        {
            var client = ExchangeClientFactory.Get(account);
            var capability = client.GetCapability<IGetTicker>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.PerpetualLinear);
            if (capability is null) return null;

            var result = await capability.Capability
                .GetTickerAsync(new GetTickerRequest(symbol), ct)
                .ConfigureAwait(false);
            return result.Success && result.Data.LastPrice > 0 ? result.Data.LastPrice : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trading] 行情拉取异常：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>设置杠杆与保证金模式（不支持时如实返回失败，由调用方决定是否继续）。</summary>
    public static async Task<(bool Ok, string? Error)> SetLeverageAsync(ExchangeAccount account, SharedSymbol symbol, decimal leverage, bool crossMargin, CancellationToken ct)
    {
        try
        {
            var client = ExchangeClientFactory.Get(account);
            var capability = client.GetCapability<ISetLeverage>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.PerpetualLinear);
            if (capability is null) return (false, "该交易所不支持杠杆设置");

            var result = await capability.Capability
                .SetLeverageAsync(
                    new SetLeverageRequest(symbol, leverage, null, crossMargin ? SharedMarginMode.Cross : SharedMarginMode.Isolated, ExchangeClientFactory.Parameters(account)),
                    ct)
                .ConfigureAwait(false);
            return result.Success ? (true, null) : (false, result.Error?.ToString() ?? "杠杆设置失败");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>市价开仓（可带止损/止盈参数；是否被接受由能力审计保证；positionSide 由持仓模式决定：双向传、单向 null）。</summary>
    public static async Task<(bool Ok, string? Error)> PlaceMarketAsync(
        ExchangeAccount account,
        SharedSymbol symbol,
        bool isLong,
        decimal quantityBase,
        decimal? stopLossPrice,
        decimal? takeProfitPrice,
        bool crossMargin,
        SharedPositionSide? positionSide,
        CancellationToken ct)
    {
        try
        {
            var client = ExchangeClientFactory.Get(account);
            var capability = client.GetCapability<IPlaceFuturesOrder>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.PerpetualLinear);
            if (capability is null) return (false, "该交易所不支持永续下单");

            var request = new PlaceFuturesOrderRequest(
                symbol,
                isLong ? SharedOrderSide.Buy : SharedOrderSide.Sell,
                SharedOrderType.Market,
                SharedQuantity.Base(quantityBase),
                null,
                false,
                null,
                null,
                positionSide,
                crossMargin ? SharedMarginMode.Cross : SharedMarginMode.Isolated,
                null,
                ExchangeClientFactory.Parameters(account))
            {
                StopLossPrice = stopLossPrice,
                TakeProfitPrice = takeProfitPrice,
            };

            var result = await capability.Capability.PlaceFuturesOrderAsync(request, ct).ConfigureAwait(false);
            return result.Success
                ? (true, null)
                : (false, result.Error?.ToString() ?? "下单失败");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "下单超时");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // —— 持仓模式（双向 HedgeMode / 单向 OneWay；10 分钟缓存，失败短缓存）——

    private static readonly ConcurrentDictionary<int, (DateTime At, SharedPositionMode? Value)> PositionModeCache = new();

    public async Task<SharedPositionMode?> GetPositionModeAsync(ExchangeAccount account, CancellationToken ct)
    {
        if (PositionModeCache.TryGetValue(account.Id, out var cached)
            && DateTime.UtcNow - cached.At < (cached.Value is not null ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30)))
        {
            return cached.Value;
        }

        SharedPositionMode? mode = null;
        try
        {
            var client = ExchangeClientFactory.Get(account);
            var capability = client.GetCapability<IGetPositionMode>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.PerpetualLinear);
            if (capability is not null)
            {
                var result = await capability.Capability
                    .GetPositionModeAsync(new GetPositionModeRequest(TradingMode.PerpetualLinear, ExchangeClientFactory.Parameters(account)), ct)
                    .ConfigureAwait(false);
                if (result.Success && result.Data is not null)
                {
                    mode = result.Data.PositionMode;
                }
                else if (!result.Success)
                {
                    Console.WriteLine($"[Trading] 持仓模式查询失败：{result.Error}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trading] 持仓模式查询异常：{ex.GetType().Name} {ex.Message}");
        }

        PositionModeCache[account.Id] = (DateTime.UtcNow, mode);
        return mode;
    }

    /// <summary>整仓平仓（ICloseFullPosition，可用时优先）。</summary>
    public static async Task<(bool Ok, string? Error)> CloseFullPositionAsync(ExchangeAccount account, SharedSymbol symbol, bool crossMargin, CancellationToken ct)
    {
        try
        {
            var client = ExchangeClientFactory.Get(account);
            var capability = client.GetCapability<ICloseFullPosition>(ExchangeClientFactory.Name(account.ExchangeType), TradingMode.PerpetualLinear);
            if (capability is null) return (false, "__unsupported__"); // 由调用方决定是否兜底

            var result = await capability.Capability
                .CloseFullPositionAsync(
                    new CloseFullPositionRequest(symbol, SharedPositionMode.OneWay, null, crossMargin ? SharedMarginMode.Cross : SharedMarginMode.Isolated, null, ExchangeClientFactory.Parameters(account)),
                    ct)
                .ConfigureAwait(false);
            return result.Success ? (true, null) : (false, result.Error?.ToString() ?? "整仓平仓失败");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 平仓（两条路径）：①交易所原生整仓平仓（ICloseFullPosition，可用时优先）；
    /// ②兜底：反向市价平仓单——按持仓模式自适应：双向持仓用 positionSide 表达只减不增（Binance 拒收 reduceOnly），
    /// 单向持仓用 ReduceOnly=true；模式未知时按错误码（-1106/-4061）自动切换重试（被拒=未成交，重试安全）。
    /// 返回被平仓位的数量与平仓时点价格（供审计留痕）。
    /// </summary>
    public async Task<(bool Ok, string? Error, decimal? Quantity, decimal? Price)> ClosePositionAsync(ExchangeAccount account, SharedSymbol symbol, bool crossMargin, CancellationToken ct)
    {
        var name = ExchangeClientFactory.Name(account.ExchangeType);
        var client = ExchangeClientFactory.Get(account);

        // 新鲜读取持仓（数量/方向用于审计与兜底）
        var positions = await FetchPositionsAsync(account, ct).ConfigureAwait(false);
        var nativeName = (await GetFuturesSymbolAsync(account, symbol, ct).ConfigureAwait(false))?.Name;
        var position = positions.FirstOrDefault(p =>
            (nativeName is not null && string.Equals(p.Symbol, nativeName, StringComparison.OrdinalIgnoreCase))
            || p.Symbol.Contains(symbol.BaseAsset, StringComparison.OrdinalIgnoreCase));
        var auditQuantity = position is null ? (decimal?)null : Math.Abs(position.Quantity);
        var auditPrice = await GetPriceAsync(account, symbol, ct).ConfigureAwait(false) ?? position?.AverageOpenPrice;

        // ① 原生整仓平仓（可用时优先）
        var (fullOk, fullError) = await CloseFullPositionAsync(account, symbol, crossMargin, ct).ConfigureAwait(false);
        if (fullOk) return (true, null, auditQuantity, auditPrice);
        var nativeError = fullError == "__unsupported__" ? null : fullError;
        if (nativeError is not null)
        {
            Console.WriteLine($"[Trading] {symbol} 整仓平仓接口失败，尝试反向市价单兜底：{nativeError}");
        }

        // ② 兜底：反向市价平仓单（持仓模式自适应）
        try
        {
            if (position is null) return (false, nativeError ?? "未找到该标的持仓", null, null);

            var quantity = Math.Abs(position.Quantity);
            if (quantity <= 0) return (false, "持仓数量为 0", null, null);

            var orderCapability = client.GetCapability<IPlaceFuturesOrder>(name, TradingMode.PerpetualLinear);
            if (orderCapability is null) return (false, nativeError ?? "该交易所不支持下单，无法兜底平仓", auditQuantity, auditPrice);

            var isLong = position.Side.ToString().Contains("Long", StringComparison.OrdinalIgnoreCase);
            var mode = await GetPositionModeAsync(account, ct).ConfigureAwait(false);
            var closeSide = isLong ? SharedOrderSide.Sell : SharedOrderSide.Buy;
            var marginMode = crossMargin ? SharedMarginMode.Cross : SharedMarginMode.Isolated;

            PlaceFuturesOrderRequest Build(bool hedgeStyle) => new(
                symbol, closeSide, SharedOrderType.Market, SharedQuantity.Base(quantity),
                null,
                hedgeStyle ? null : true,
                null, null,
                hedgeStyle ? (isLong ? SharedPositionSide.Long : SharedPositionSide.Short) : null,
                marginMode, null,
                ExchangeClientFactory.Parameters(account));

            // 预判：双向持仓 → 先 positionSide 形态；单向/未知 → 先 ReduceOnly 形态；错误码不匹配自动换另一种
            var attempts = mode == SharedPositionMode.HedgeMode ? new[] { true, false } : new[] { false, true };

            string? lastError = null;
            foreach (var hedgeStyle in attempts)
            {
                var order = await orderCapability.Capability.PlaceFuturesOrderAsync(Build(hedgeStyle), ct).ConfigureAwait(false);
                if (order.Success)
                {
                    var style = hedgeStyle ? "positionSide·双向持仓" : "ReduceOnly·单向持仓";
                    Console.WriteLine($"[Trading] {symbol} 已通过反向市价单平仓（数量 {quantity}，{style}）");
                    PositionCache.TryRemove(account.Id, out _);
                    return (true, null, quantity, auditPrice);
                }

                lastError = order.Error?.ToString() ?? "下单失败";
                var retriable = lastError.Contains("-1106", StringComparison.Ordinal)
                    || lastError.Contains("-4061", StringComparison.Ordinal)
                    || lastError.Contains("reduceonly", StringComparison.OrdinalIgnoreCase)
                    || lastError.Contains("position side", StringComparison.OrdinalIgnoreCase);
                if (!retriable) break;
                Console.WriteLine($"[Trading] {symbol} 平仓形态与持仓模式不匹配（{lastError}），切换另一种重试");
            }

            return (false, $"兜底平仓失败：{lastError}", auditQuantity, auditPrice);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "平仓超时", auditQuantity, auditPrice);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, auditQuantity, auditPrice);
        }
    }

    // —— USDT 永续目录（下单定量与原生名反解用；10 分钟缓存）——

    private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<SharedFuturesSymbol> Value)> CatalogCache = new();
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(10);

    /// <summary>按值对象在 USDT 永续目录中解析标的元数据（数量步进/最小额/最大杠杆）。</summary>
    public static async Task<SharedFuturesSymbol?> GetFuturesSymbolAsync(ExchangeAccount account, SharedSymbol symbol, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(account, ct).ConfigureAwait(false);
        return catalog.FirstOrDefault(s => s.BaseAsset == symbol.BaseAsset && s.QuoteAsset == symbol.QuoteAsset);
    }

    /// <summary>按交易所原生符号名（如 BTCUSDT / BTC_USDT）反解 SharedSymbol（持仓行平仓等场景）。</summary>
    public static async Task<SharedSymbol?> ResolveSymbolByNameAsync(ExchangeAccount account, string name, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(account, ct).ConfigureAwait(false);
        return catalog.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.SharedSymbol;
    }

    private static async Task<IReadOnlyList<SharedFuturesSymbol>> GetCatalogAsync(ExchangeAccount account, CancellationToken ct)
    {
        var key = ExchangeClientFactory.Name(account.ExchangeType);
        if (CatalogCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < (cached.Value.Count > 0 ? CatalogTtl : TimeSpan.FromSeconds(30)))
        {
            return cached.Value;
        }

        IReadOnlyList<SharedFuturesSymbol> value = [];
        try
        {
            var symbolsClient = new ExchangeRestClient();
            var result = await symbolsClient
                .GetFuturesSymbolsAsync(key, new GetSymbolsRequest(TradingMode.PerpetualLinear, exchangeParameters: ExchangeClientFactory.Parameters(account)), ct)
                .ConfigureAwait(false);
            if (result.Success && result.Data is not null)
            {
                value = result.Data.Where(s => s.QuoteAsset == "USDT").ToList();
            }
            else if (!result.Success)
            {
                Console.WriteLine($"[Trading] {key} 标的目录拉取失败：{result.Error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trading] {key} 标的目录拉取异常：{ex.GetType().Name} {ex.Message}");
        }

        CatalogCache[key] = (DateTime.UtcNow, value);
        return value;
    }
}
