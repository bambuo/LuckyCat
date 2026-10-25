using Microsoft.EntityFrameworkCore;
using LuckyCat.Data;

namespace LuckyCat.Services;

/// <summary>
/// 启动恢复宿主（LC-STD-01 §8.7，Task 14 B 切流后的唯一启动链）：应用启动时按顺序做两件事——
/// ① <see cref="TradingCycleRunner.RecoverAsync"/>：把遗留 <c>running</c> 周期收敛为
/// <c>aborted</c> + <c>process_restart</c>（LC-STD-05 FLOW-13）；
/// ② 读取 <c>IsRunning</c> 交易员并逐个 <see cref="TradingCycleRunner.Start"/> 恢复调度
/// （§8.7「启动恢复：读取 IsRunning 交易员恢复调度」），每个交易员恰好一次——
/// <see cref="TradingCycleRunner.Start"/> 对同交易员幂等，双循环不可能在本进程内出现（R-11-6 的教训）。
/// <para>
/// 顺序不可颠倒：先收敛遗留 running 再起重复循环，避免旧周期行在新周期开始后仍挂着 running。
/// 单个交易员恢复失败只写服务端日志（脱敏，LC-STD-02 §10）、绝不阻断启动或其他交易员的恢复；
/// 恢复本身失败同样不阻断启动（与原 ExecuteAsync 的 try/catch 口径一致）。
/// </para>
/// </summary>
public sealed class CycleStartupRecovery(TradingCycleRunner runner, IServiceScopeFactory scopeFactory)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runner.RecoverAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cycle] recover：{LogRedaction.Scrub(ex.ToString())}");
        }

        await ResumeRunningTradersAsync(stoppingToken);
    }

    /// <summary>为 IsRunning 交易员恢复新链路调度；每交易员独立 try，失败只记日志。</summary>
    private async Task ResumeRunningTradersAsync(CancellationToken ct)
    {
        List<int> traderIds;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            traderIds = await db.Traders
                .Where(t => t.IsRunning)
                .OrderBy(t => t.Id)
                .Select(t => t.Id)
                .ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[Cycle] 读取 IsRunning 交易员失败：{LogRedaction.Scrub(ex.ToString())}");
            return;
        }

        var resumed = 0;
        foreach (var traderId in traderIds)
        {
            try
            {
                runner.Start(traderId);
                resumed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个交易员恢复失败绝不影响其余交易员，也绝不阻断启动（不裸 catch OCE：停机令牌要向上收敛）。
                Console.WriteLine($"[Cycle] trader:{traderId} 启动恢复调度失败：{LogRedaction.Scrub(ex.ToString())}");
            }
        }

        if (resumed > 0)
        {
            Console.WriteLine($"[Cycle] 启动恢复：{resumed} 个 IsRunning 交易员已恢复新链路调度");
        }
    }
}
