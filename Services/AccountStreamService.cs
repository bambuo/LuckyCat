using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.SharedApis;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using LuckyCat.Data;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>账户实时状态（SSE 推送负载）：持仓/净值由交易所 WS 订阅驱动；价格优先「标记价」订阅（未实现盈亏的正确基准），不可用时回退行情最新价。</summary>
public record AccountRealtimeState(
    int Version,
    DateTime At,
    IReadOnlyList<SymbolPosition> Positions,
    decimal? Equity,
    decimal? Available,
    IReadOnlyDictionary<string, decimal> Marks,
    string? PriceType);

/// <summary>
/// 账户实时流服务：按账户维护 WS 订阅（持仓 ISubscribePositionsSocket / 余额 ISubscribeBalancesSocket /
/// 持仓标的行情 ISubscribeTickerSocket），以引用计数随 SSE 订阅者启停；状态带版本号供 SSE 节流推送。
/// </summary>
public class AccountStreamService : IAsyncDisposable
{
    private sealed class AccountSubscription
    {
        public readonly object Gate = new();
        public int RefCount;
        public UpdateSubscription? Positions;
        public UpdateSubscription? Balances;
        public readonly Dictionary<string, UpdateSubscription> Tickers = new(StringComparer.Ordinal);
        public List<SymbolPosition> Live = [];
        public decimal? Equity;
        public decimal? Available;
        public readonly Dictionary<string, decimal> Marks = new(StringComparer.Ordinal);
        public string? PriceType;
        public int Version;
        public DateTime At = DateTime.UtcNow;
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SseEventHub _hub;
    private readonly ConcurrentDictionary<int, AccountSubscription> _states = new();

    public AccountStreamService(IServiceScopeFactory scopeFactory, SseEventHub hub)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
    }

    /// <summary>引用计数 +1；首个订阅者时以 REST 播种首帧并拉起 WS 订阅。</summary>
    public async Task AcquireAsync(ExchangeAccount account, CancellationToken ct)
    {
        var sub = _states.GetOrAdd(account.Id, _ => new AccountSubscription());
        bool first;
        lock (sub.Gate)
        {
            first = sub.RefCount++ == 0;
        }
        if (!first) return;

        await SeedFromRestAsync(account, sub, ct).ConfigureAwait(false);
        await StartWebSocketsAsync(account, sub, ct).ConfigureAwait(false);
    }

    /// <summary>引用计数 -1；最后一个订阅者离开时释放全部 WS 订阅。</summary>
    public void Release(int accountId)
    {
        if (!_states.TryGetValue(accountId, out var sub)) return;
        bool last;
        lock (sub.Gate)
        {
            last = --sub.RefCount <= 0;
        }
        if (!last) return;

        _states.TryRemove(accountId, out _);
        _ = CloseAllAsync(sub);
        Console.WriteLine($"[Stream] 账户 {accountId} 实时订阅已释放");
    }

    public AccountRealtimeState? GetState(int accountId)
    {
        if (!_states.TryGetValue(accountId, out var sub)) return null;
        lock (sub.Gate)
        {
            return new AccountRealtimeState(
                sub.Version,
                sub.At,
                sub.Live.ToList(),
                sub.Equity,
                sub.Available,
                new Dictionary<string, decimal>(sub.Marks, StringComparer.Ordinal),
                sub.PriceType);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _states.Keys.ToList())
        {
            await CloseAllAsync(_states[id]).ConfigureAwait(false);
        }
        _states.Clear();
    }

    // —— 内部实现 ——

    private async Task SeedFromRestAsync(ExchangeAccount account, AccountSubscription sub, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var trading = scope.ServiceProvider.GetRequiredService<ExchangeTradingService>();
            var positions = await trading.GetPositionsAsync(account, ct).ConfigureAwait(false);
            var snapshot = await trading.GetAccountAsync(account, ct).ConfigureAwait(false);
            lock (sub.Gate)
            {
                sub.Live = positions.ToList();
                if (snapshot.Success)
                {
                    sub.Equity = snapshot.UsdtEquity;
                    sub.Available = snapshot.UsdtAvailable;
                }
                sub.Version++;
                sub.At = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Stream] 账户 {account.Id} 首帧播种失败：{ex.Message}");
        }
    }

    private async Task StartWebSocketsAsync(ExchangeAccount account, AccountSubscription sub, CancellationToken ct)
    {
        var client = ExchangeClientFactory.Get(account);
        var name = ExchangeClientFactory.Name(account.ExchangeType);
        var parameters = ExchangeClientFactory.Parameters(account);

        // 持仓订阅
        var positionsCapability = client.GetCapability<ISubscribePositionsSocket>(name, TradingMode.PerpetualLinear);
        if (positionsCapability is not null)
        {
            try
            {
                var result = await positionsCapability.Capability
                    .SubscribeToPositionUpdatesAsync(
                        new SubscribePositionRequest(TradingMode.PerpetualLinear, parameters),
                        data => OnPositions(account, sub, data.Data),
                        ct)
                    .ConfigureAwait(false);
                if (result.Success)
                {
                    sub.Positions = result.Data;
                    Console.WriteLine($"[Stream] 账户 {account.Id} 持仓订阅已建立");
                }
                else
                {
                    Console.WriteLine($"[Stream] 账户 {account.Id} 持仓订阅失败：{result.Error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stream] 账户 {account.Id} 持仓订阅异常：{ex.GetType().Name} {ex.Message}");
            }
        }

        // 余额订阅
        var balancesCapability = client.GetCapability<ISubscribeBalancesSocket>(name, TradingMode.PerpetualLinear);
        if (balancesCapability is not null)
        {
            try
            {
                var result = await balancesCapability.Capability
                    .SubscribeToBalanceUpdatesAsync(
                        new SubscribeBalancesRequest(TradingMode.PerpetualLinear, parameters),
                        data => OnBalances(account, sub, data.Data),
                        ct)
                    .ConfigureAwait(false);
                if (result.Success)
                {
                    sub.Balances = result.Data;
                    Console.WriteLine($"[Stream] 账户 {account.Id} 余额订阅已建立");
                }
                else
                {
                    Console.WriteLine($"[Stream] 账户 {account.Id} 余额订阅失败：{result.Error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stream] 账户 {account.Id} 余额订阅异常：{ex.GetType().Name} {ex.Message}");
            }
        }

        // 持仓标的行情订阅（未实现盈亏实时化的数据源）
        await SyncPriceSubscriptionsAsync(account, sub, ct).ConfigureAwait(false);
    }

    private void OnPositions(ExchangeAccount account, AccountSubscription sub, SharedPosition[] positions)
    {
        lock (sub.Gate)
        {
            foreach (var pushed in positions)
            {
                var quantity = pushed.PositionSizes?.QuantityInBaseAsset ?? pushed.PositionSizes?.QuantityInContracts ?? 0m;
                var symbol = pushed.Symbol ?? "";
                if (string.IsNullOrEmpty(symbol)) continue;

                var index = sub.Live.FindIndex(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
                if (quantity == 0)
                {
                    if (index >= 0) sub.Live.RemoveAt(index);
                    continue;
                }

                var item = new SymbolPosition(
                    symbol,
                    pushed.PositionSide,
                    quantity,
                    pushed.AverageOpenPrice ?? 0m,
                    pushed.UnrealizedPnl,
                    pushed.LiquidationPrice,
                    pushed.Leverage);
                if (index >= 0) sub.Live[index] = item;
                else sub.Live.Add(item);
            }

            sub.Version++;
            sub.At = DateTime.UtcNow;
        }

        // 持仓变化 → 同步 ticker 订阅集合（增/删标的）
        _ = SyncPriceSubscriptionsAsync(account, sub, CancellationToken.None);
    }

    private void OnBalances(ExchangeAccount account, AccountSubscription sub, SharedBalance[] balances)
    {
        var usdt = balances.FirstOrDefault(b => string.Equals(b.Asset, "USDT", StringComparison.OrdinalIgnoreCase));
        if (usdt is null) return;
        decimal equity;
        lock (sub.Gate)
        {
            sub.Equity = usdt.Total;
            sub.Available = usdt.Available;
            sub.Version++;
            sub.At = DateTime.UtcNow;
            equity = usdt.Total + sub.Live.Sum(p => p.UnrealizedPnl ?? 0m);
        }

        // 事件驱动净值采样：余额变化事件（已实现盈亏/资金费/划转/订阅首帧）即时落库一个净值点
        _ = PersistEquitySampleAsync(account, equity);
    }

    private static readonly ConcurrentDictionary<int, DateTime> LastEquitySampleAt = new();

    private async Task PersistEquitySampleAsync(ExchangeAccount account, decimal equity)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (LastEquitySampleAt.TryGetValue(account.Id, out var last) && now - last < TimeSpan.FromSeconds(5))
            {
                return; // 5 秒内不重复落库（熔断高频事件）
            }
            LastEquitySampleAt[account.Id] = now;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var latest = await db.AccountEquitySamples
                .Where(s => s.ExchangeAccountId == account.Id)
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync();
            if (latest is not null && latest.Equity == equity && now - latest.At < TimeSpan.FromMinutes(2))
            {
                return;
            }

            var sample = new AccountEquitySample
            {
                ExchangeAccountId = account.Id,
                At = now,
                Equity = equity,
            };
            db.AccountEquitySamples.Add(sample);
            await db.SaveChangesAsync();
            _hub.Publish(SseChannels.Account(account.Id), new SseEvent("equity", sample));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Stream] 净值事件落库失败：{ex.Message}");
        }
    }

    private void OnMark(AccountSubscription sub, string nativeSymbol, decimal? price)
    {
        if (price is not > 0) return;
        lock (sub.Gate)
        {
            sub.Marks[nativeSymbol] = price.Value;
            sub.Version++;
            sub.At = DateTime.UtcNow;
        }
    }

    private async Task SyncPriceSubscriptionsAsync(ExchangeAccount account, AccountSubscription sub, CancellationToken ct)
    {
        var client = ExchangeClientFactory.Get(account);
        var name = ExchangeClientFactory.Name(account.ExchangeType);
        var parameters = ExchangeClientFactory.Parameters(account);

        // 优先「标记价」订阅（未实现盈亏的正确基准）；不可用时回退行情最新价并如实标注
        var markCapability = client.GetCapability<ISubscribeMarkPriceSocket>(name, TradingMode.PerpetualLinear);
        var tickerCapability = markCapability is null
            ? client.GetCapability<ISubscribeTickerSocket>(name, TradingMode.PerpetualLinear)
            : null;
        if (markCapability is null && tickerCapability is null) return;
        lock (sub.Gate)
        {
            sub.PriceType = markCapability is not null ? "mark" : "last";
        }

        List<string> targets;
        lock (sub.Gate)
        {
            targets = sub.Live.Select(p => p.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // 移除已无持仓的标的
        List<string> stale;
        lock (sub.Gate)
        {
            stale = sub.Tickers.Keys.Where(k => !targets.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        foreach (var symbol in stale)
        {
            UpdateSubscription? subscription = null;
            lock (sub.Gate)
            {
                if (sub.Tickers.Remove(symbol, out var found)) subscription = found;
            }
            if (subscription is not null)
            {
                try { await subscription.CloseAsync().ConfigureAwait(false); } catch { /* 忽略关闭错误 */ }
            }
        }

        // 新增缺失的标的
        foreach (var native in targets)
        {
            lock (sub.Gate)
            {
                if (sub.Tickers.ContainsKey(native)) continue;
            }

            var sharedSymbol = await ExchangeTradingService.ResolveSymbolByNameAsync(account, native, ct).ConfigureAwait(false);
            if (sharedSymbol is null)
            {
                Console.WriteLine($"[Stream] 无法解析 {native} 的共享标的，跳过价格订阅");
                continue;
            }

            try
            {
                UpdateSubscription? created = null;
                if (markCapability is not null)
                {
                    var result = await markCapability.Capability
                        .SubscribeToMarkPriceUpdatesAsync(
                            new SubscribeMarkPriceRequest(sharedSymbol, parameters),
                            data => OnMark(sub, native, data.Data.MarkPrice),
                            ct)
                        .ConfigureAwait(false);
                    if (result.Success) created = result.Data;
                    else Console.WriteLine($"[Stream] {native} 标记价订阅失败：{result.Error}");
                }
                else
                {
                    var result = await tickerCapability!.Capability
                        .SubscribeToTickerUpdatesAsync(
                            new SubscribeTickerRequest(sharedSymbol, parameters),
                            data => OnMark(sub, native, data.Data.LastPrice),
                            ct)
                        .ConfigureAwait(false);
                    if (result.Success) created = result.Data;
                    else Console.WriteLine($"[Stream] {native} 行情订阅失败：{result.Error}");
                }

                if (created is not null)
                {
                    lock (sub.Gate)
                    {
                        sub.Tickers[native] = created;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stream] {native} 价格订阅异常：{ex.GetType().Name} {ex.Message}");
            }
        }
    }

    private static async Task CloseAllAsync(AccountSubscription sub)
    {
        UpdateSubscription? positions;
        UpdateSubscription? balances;
        List<UpdateSubscription> tickers;
        lock (sub.Gate)
        {
            positions = sub.Positions;
            balances = sub.Balances;
            tickers = sub.Tickers.Values.ToList();
            sub.Positions = null;
            sub.Balances = null;
            sub.Tickers.Clear();
        }

        foreach (var subscription in new[] { positions, balances }.Where(s => s is not null).Cast<UpdateSubscription>().Concat(tickers))
        {
            try { await subscription.CloseAsync().ConfigureAwait(false); } catch { /* 忽略关闭错误 */ }
        }
    }
}
