using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>
/// 自维护仓位历史同步器（nofx `StartOrderSync` 的等价物）：
/// 启动即同步一次，之后每 **5 秒**增量同步各交易所账户的仓位历史（来源：Binance 成交明细 FIFO 重建 /
/// 其余交易所共享接口持仓历史），按稳定指纹去重落库到 `PositionHistories`——不依赖交易所 API 的保留窗口。
/// 看板打开时也会触发一次账户同步（交易所侧读取有 4 秒缓存，与新间隔匹配）。
/// 账户级失败退避：连续失败（如 Key 鉴权失效）转入 10 分钟退避——不再每 5 秒白打交易所与刷日志。
/// </summary>
public class PositionHistorySyncService(IServiceScopeFactory scopeFactory, SseEventHub hub) : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

    // 账户级退避窗口：失败后截至时间（退避期内跳过同步；到期自动重试）
    private readonly ConcurrentDictionary<int, DateTime> _backoffUntil = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cycle = 0;
        var startedAt = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                cycle++;
                var addedTotal = 0;
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var accounts = await db.ExchangeAccounts.ToListAsync(stoppingToken);
                foreach (var account in accounts)
                {
                    // 失败退避：退避期内跳过（不调用交易所、不刷日志）；到期自动重试
                    if (_backoffUntil.TryGetValue(account.Id, out var until) && DateTime.UtcNow < until)
                    {
                        continue;
                    }

                    var error = await SyncAccountAsync(db, account, stoppingToken);
                    if (error is not null)
                    {
                        var wasBackoffExpired = _backoffUntil.TryGetValue(account.Id, out var expired) && DateTime.UtcNow >= expired;
                        _backoffUntil[account.Id] = DateTime.UtcNow + FailureBackoff;
                        Console.WriteLine(wasBackoffExpired
                            ? $"[PositionSync] 账户 {account.Id}（{account.AccountName}）重试仍失败，继续退避 {FailureBackoff.TotalMinutes:0} 分钟：{error}"
                            : $"[PositionSync] 账户 {account.Id}（{account.AccountName}）同步失败，进入退避 {FailureBackoff.TotalMinutes:0} 分钟：{error}");
                    }
                    else if (_backoffUntil.TryRemove(account.Id, out _))
                    {
                        Console.WriteLine($"[PositionSync] 账户 {account.Id}（{account.AccountName}）同步已恢复");
                    }

                    // 净值采样：启动即采一次，之后每 12 周期（约 60 秒）一次——看板净值曲线不依赖交易员运行
                    if (cycle == 1 || cycle % 12 == 0)
                    {
                        await SampleEquityAsync(db, account, stoppingToken);
                    }
                }

                // 心跳：每 12 个周期（约 1 分钟）打印一次，便于观测 5 秒节奏且不刷屏
                if (cycle % 12 == 0)
                {
                    var elapsed = DateTime.UtcNow - startedAt;
                    Console.WriteLine($"[PositionSync] 心跳：已运行 {cycle} 个周期，用时 {elapsed.TotalSeconds:0}s（约 {elapsed.TotalSeconds / cycle:0.#}s/周期），最近新增 {addedTotal} 条");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PositionSync] 同步循环异常：{ex.GetType().Name} {ex.Message}");
            }

            try
            {
                await Task.Delay(SyncInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>同步单个账户的历史仓位（增量去重落库）；返回 null 表示成功，否则为错误信息。</summary>
    public async Task<string?> SyncAccountAsync(AppDbContext db, ExchangeAccount account, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var trading = scope.ServiceProvider.GetRequiredService<ExchangeTradingService>();
            var result = await trading.GetPositionHistoryAsync(account, 200, ct).ConfigureAwait(false);
            if (result.Error is not null) return result.Error; // 失败如实上抛（触发退避/提示），不再被「空结果=成功」吞掉
            if (result.Entries.Count == 0) return null;

            var existingKeys = (await db.PositionHistories
                    .Where(p => p.ExchangeAccountId == account.Id)
                    .Select(p => p.DedupeKey)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var now = DateTime.UtcNow;
            var added = 0;
            var addedRows = new List<PositionHistory>();
            foreach (var entry in result.Entries)
            {
                var key = BuildDedupeKey(account.Id, entry);
                if (existingKeys.Contains(key)) continue;

                var row = new PositionHistory
                {
                    ExchangeAccountId = account.Id,
                    DedupeKey = key,
                    Source = entry.Source,
                    Symbol = entry.Symbol,
                    Side = entry.Side,
                    Quantity = entry.Quantity,
                    EntryPrice = entry.EntryPrice,
                    ExitPrice = entry.ExitPrice,
                    RealizedPnl = entry.RealizedPnl,
                    Fee = entry.Fee,
                    Leverage = null, // 展示时经「持仓采样 → 当前杠杆设置」回填
                    OpenTime = entry.OpenTime,
                    CloseTime = entry.Time,
                    SyncedAt = now,
                };
                db.PositionHistories.Add(row);
                addedRows.Add(row);
                existingKeys.Add(key);
                added++;
            }

            if (added > 0)
            {
                await db.SaveChangesAsync(ct);
                Console.WriteLine($"[PositionSync] 账户 {account.Id} 新增 {added} 条历史仓位（累计 {existingKeys.Count} 条）");
                // 历史发生变化：按 §4.2 position-history 载荷逐行通知看板连接
                // （由前端触发一次聚合刷新，替代轮询；SaveChanges 后自增 Id 已回填）。
                foreach (var row in addedRows)
                {
                    hub.Publish(SseChannels.Account(account.Id), new SseEvent("position-history",
                        new PositionHistoryEventDto(row.Id, row.Symbol,
                            row.RealizedPnl.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            row.CloseTime)));
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // 交易所侧事件的稳定指纹：同一仓位重复同步不产生重复行
    private static string BuildDedupeKey(int accountId, PositionHistoryEntry entry) =>
        $"{accountId}|{entry.Source}|{entry.Symbol}|{entry.Time:O}|{entry.Quantity:0.########}|{entry.EntryPrice:0.########}|{entry.ExitPrice:0.########}|{entry.RealizedPnl:0.########}";

    /// <summary>
    /// 账户净值采样（钱包余额 + 未实现盈亏，与看板展示口径一致）：与最近样本无变化且较新（&lt;10 分钟）时跳过——
    /// 平静时段避免无效行增长，同时保证至少每 10 分钟一个数据点。
    /// </summary>
    public async Task SampleEquityAsync(AppDbContext db, ExchangeAccount account, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var trading = scope.ServiceProvider.GetRequiredService<ExchangeTradingService>();
            var snapshot = await trading.GetAccountAsync(account, ct).ConfigureAwait(false);
            if (!snapshot.Success) return;

            var positions = await trading.GetPositionsAsync(account, ct).ConfigureAwait(false);
            var equity = snapshot.UsdtEquity + positions.Sum(p => p.UnrealizedPnl ?? 0m);

            var latest = await db.AccountEquitySamples
                .Where(s => s.ExchangeAccountId == account.Id)
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync(ct);
            if (latest is not null && latest.Equity == equity && DateTime.UtcNow - latest.At < TimeSpan.FromMinutes(10))
            {
                return;
            }

            var sample = new AccountEquitySample
            {
                ExchangeAccountId = account.Id,
                At = DateTime.UtcNow,
                Equity = equity,
            };
            db.AccountEquitySamples.Add(sample);
            await db.SaveChangesAsync(ct);
            hub.Publish(SseChannels.Account(account.Id), new SseEvent("equity", sample));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PositionSync] 净值采样失败：{ex.Message}");
        }
    }
}
