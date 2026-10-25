namespace LuckyCat.Services;

/// <summary>
/// 迁移期启动恢复宿主（R-11-6）：应用启动时调用一次
/// <see cref="TradingCycleRunner.RecoverAsync"/>——把遗留 <c>running</c> 周期标记
/// <c>aborted</c> + <c>process_restart</c>（LC-STD-01 §8.7、LC-STD-05 FLOW-13），
/// 除此之外不做任何事：<b>绝不启动交易员调度循环</b>。迁移期唯一的自动调度链仍是旧的
/// <see cref="TraderRunnerService"/>，双链路同时调度会造成同一交易员双重开仓（交易安全缺陷）；
/// Task 18 切流时删除本类、把 <see cref="TradingCycleRunner"/> 重新注册为 hosted 调度并恢复
/// 「重启后自动续跑 IsRunning 交易员」的语义。
/// <para>
/// 恢复失败只写服务端日志、绝不阻断启动（与原 ExecuteAsync 的 try/catch 口径一致，
/// 异常原文只进日志，不落库、不进 SSE，LC-STD-02 §10）。
/// </para>
/// </summary>
public sealed class CycleStartupRecovery(TradingCycleRunner runner) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runner.RecoverAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cycle] recover：{ex}");
        }
    }
}
