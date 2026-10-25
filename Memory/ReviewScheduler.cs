using System.Globalization;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.EntityFrameworkCore;

namespace LuckyCat.Memory;

/// <summary>
/// 复盘调度宿主（LC-STD-01 §9「触发」行的定时那一半；R-13-1）：
/// <b>只做三件事</b>——分钟 tick → 判定「今天该不该跑」→ 派发给 <see cref="ReviewRunner"/>，
/// 复盘本身（窗口、取数、模型、落库）一律不在本类里，保持单一职责。
/// <para>
/// 到点判定（R-13-2，与 <see cref="TradingRiskPolicy.EvaluateDiscipline"/> 的纪律窗口同一时区口径）：
/// <c>review.localTime</c> 是<b>本地</b> HH:mm，比较用 <see cref="TimeZoneInfo.Local"/>；
/// 判据是「本地时刻已过 today 的 localTime」而不是「本地时刻 == localTime」，
/// 于是 00:10 停机、当天 09:00 起来能<b>补跑一次</b>；窗口从上次成功复盘起算（最多 30 天），
/// 补跑不丢数据也不重复覆盖。
/// </para>
/// <para>
/// 「每交易员每本地日最多一次」的守卫<b>只从持久数据推导</b>（重启安全，R-13-2）：
/// 查当天（本地日）是否已有 role=reviewer 的 <see cref="AgentStageRun"/> 行——completed / failed /
/// skipped 三种都算「本轮触发已经发生」，因此 09:00 重启不会把 00:10 那次再跑一遍；
/// 当天没有任何复盘行时才补跑一次。进程内状态一律不参与判定（<see cref="ReviewRunner"/> 的
/// 同交易员闸只防「正在进行中」的并发，不防跨日重复）。
/// </para>
/// <para>
/// 开关（R-13-4）：<c>review.enabled=false</c> 的交易员永不派发；
/// <see cref="ReviewRunner.RunAsync"/> 本身不看该开关，手动端点（Task 14）仍可触发。
/// </para>
/// <para>
/// 失败与并发（§9 失败行、R-13-5）：单个交易员派发失败只记日志，继续派发下一个；
/// 交易员之间顺序执行（每日批处理，串行即可，不并发写库）。本类<b>不</b>依赖
/// <see cref="TradingCycleRunner"/>、不占用交易周期闸，因此复盘不会拖慢交易调度；
/// 异常原文只进服务端日志（R-13-7）。
/// </para>
/// <para>
/// hosted 注册说明：R-11-6 禁止的是迁移期把<b>交易</b>宿主注册为 hosted 调度
/// （双链路会对同一交易员双重开仓）。复盘员不交易、不下单、不改配置（§1.35），
/// 把它注册为 hosted 不构成第二条交易调度链，因此这里 <b>应当</b> 托管（LC-STD-01 §9 的
/// 「本地时刻（默认 00:10）」触发只能由宿主驱动）。
/// </para>
/// </summary>
public sealed class ReviewScheduler(ReviewRunner runner, IServiceScopeFactory scopeFactory) : BackgroundService
{
    /// <summary>tick 粒度：分钟（§9 的触发精度就是分钟级本地时刻）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // 对齐到分钟边界后再 tick，避免每次 tick 的秒数漂移。
            await DelayToNextMinute(stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;
            try
            {
                var dispatched = await RunTickAsync(DateTime.UtcNow, stoppingToken);
                if (dispatched > 0) Console.WriteLine($"[Review] 定时派发复盘 {dispatched} 个交易员");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 调度本身失败只影响今天的复盘，绝不影响交易链路（§9 失败行）；原文只进日志。
                Console.WriteLine($"[Review] tick：{ex}");
            }
        }
    }

    /// <summary>睡到下一个整分钟边界（取消即返回；不 busy-wait）。</summary>
    private static async Task DelayToNextMinute(CancellationToken ct)
    {
        var now = DateTime.Now;
        var next = now.Date.AddMinutes(Math.Ceiling((now - now.Date).TotalMinutes / TickInterval.TotalMinutes)
            * TickInterval.TotalMinutes);
        var delay = next - now;
        if (delay <= TimeSpan.Zero) delay = TickInterval;
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // 停机：由调用方在取消检查处退出循环。
        }
    }

    /// <summary>
    /// 一次分钟 tick：对每个 <c>IsRunning</c> 交易员按「开关 → 到点? → 当天已跑?」三段判定，
    /// 命中即派发复盘。返回实际派发的交易员数（被同交易员进行中闸拒绝的不计，§9 不并行写同一交易员）。
    /// 公开以便测试与手动补跑复用同一判据（时钟由调用方传入，生产传 <see cref="DateTime.UtcNow"/>）。
    /// </summary>
    public async Task<int> RunTickAsync(DateTime nowUtc, CancellationToken ct)
    {
        var now = AsUtc(nowUtc);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var traders = await db.Traders.AsNoTracking()
            .Where(trader => trader.IsRunning)
            .OrderBy(trader => trader.Id)
            .ToListAsync(ct);
        if (traders.Count == 0) return 0;

        // 复盘开关与本地时刻都在策略配置里：按交易员绑定的策略逐一取，不用任何一份全局默认值。
        var strategyIds = traders.Select(trader => trader.StrategyId).Distinct().ToList();
        var configs = await db.Strategies.AsNoTracking()
            .Where(strategy => strategyIds.Contains(strategy.Id))
            .ToDictionaryAsync(strategy => strategy.Id, strategy => strategy.ConfigJson, ct);

        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var dispatched = 0;
        foreach (var trader in traders)
        {
            if (ct.IsCancellationRequested) break;
            if (!IsDue(trader, localNow, configs)) continue;
            if (await LandedTodayAsync(db, trader.Id, localNow.Date, now, ct)) continue;

            try
            {
                var result = await runner.RunAsync(trader, ct);
                if (result.Status != ReviewRunner.InProgressStatus) dispatched++;
            }
            catch (Exception ex)
            {
                // 一个交易员的复盘失败/抛错不影响其他交易员（§9「不阻塞交易、下个触发点重试」）。
                Console.WriteLine($"[Review] trader:{trader.Id} 派发异常：{ex}");
            }
        }

        return dispatched;
    }

    /// <summary>
    /// 到点判定：① 策略配置缺失或 <c>review.enabled=false</c> → 永不派发（R-13-4）；
    /// ② <c>localTime</c> 解析失败（脏配置）→ 容忍，本轮不派发，不抛给整个 tick；
    /// ③ 本地时刻已过了当天的 localTime 才算到点（含补跑语义）。
    /// </summary>
    private static bool IsDue(Trader trader, DateTime localNow, Dictionary<int, string> configs)
    {
        if (!configs.TryGetValue(trader.StrategyId, out var configJson)) return false;
        ReviewConfig? review;
        try
        {
            review = JsonSerializer.Deserialize<StrategyConfig>(configJson, VerificationJson.Options)?.Review;
        }
        catch (JsonException)
        {
            return false;
        }

        return review is { Enabled: true } && TryParseLocalTime(review.LocalTime, out var timeOfDay)
                                       && localNow.TimeOfDay >= timeOfDay;
    }

    /// <summary>
    /// 当天（<paramref name="localNow"/> 所在的本地日）是否已有复盘阶段行落库——重启安全的唯一判据。
    /// 先按 UTC 粗筛（回看 2 天足够覆盖任何时区偏移，因为同一天内的任意两个时刻相距不到 25 小时），
    /// 再把每行的 UTC 时刻折成本地日期比对，避免把时区换算写进 SQL。
    /// </summary>
    private static async Task<bool> LandedTodayAsync(AppDbContext db, int traderId, DateTime localDate,
        DateTime nowUtc, CancellationToken ct)
    {
        var candidates = await db.AgentStageRuns.AsNoTracking()
            .Where(run => run.Role == ToolRegistry.ReviewerRoleName
                && run.TraderId == traderId
                && run.CreatedAt >= nowUtc.AddDays(-2))
            .Select(run => run.CreatedAt)
            .ToListAsync(ct);

        return candidates.Any(createdAt =>
            TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc), TimeZoneInfo.Local).Date ==
            localDate.Date);
    }

    /// <summary>本地 HH:mm 解析（严格两位数字，拒绝 24:00 之类的越界值——脏配置一律按「不派发」处理）。</summary>
    private static bool TryParseLocalTime(string? value, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (TimeSpan.TryParseExact((value ?? "").Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var parsed)
            && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
        {
            timeOfDay = parsed;
            return true;
        }

        return false;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
