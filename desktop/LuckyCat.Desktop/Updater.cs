using Avalonia.Controls;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace LuckyCat.Desktop;

public static class Updater
{
    // 公开仓库匿名读取 GitHub Releases：限额 60 次/小时，远高于 6 小时一次的检查频率。
    private static readonly UpdateManager Manager =
        new(new GithubSource("https://github.com/bambuo/LuckyCat", accessToken: null, prerelease: false));

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private const string RestartMessage =
        "重启后生效。若交易员正在运行，重启会中断当前周期；重启后运行中的交易员会自动恢复调度。";

    private static int _checking;
    private static NoticeWindow? _prompt;

    public static async Task CheckNowAsync(Window owner)
    {
        if (!Manager.IsInstalled) return;

        // 托盘手动检查与定时检查可能重叠：同一时刻只跑一份，避免重复下载与重复提示。
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;

        try
        {
            var update = await Manager.CheckForUpdatesAsync();
            if (update is null) return;

            await Manager.DownloadUpdatesAsync(update);
            await Dispatcher.UIThread.InvokeAsync(() => PromptRestart(owner, update));
        }
        catch (Exception ex)
        {
            // 更新失败不打断交易：只落日志，不弹错误窗。
            Console.WriteLine($"[Updater] 检查或下载更新失败：{ex}");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    public static async Task StartPeriodic(Window owner, CancellationToken ct)
    {
        if (!Manager.IsInstalled)
        {
            Console.WriteLine("[Updater] 当前不是 Velopack 安装版本，自动更新已停用。");
            return;
        }

        try
        {
            await Task.Delay(FirstCheckDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckNowAsync(owner);
                // 检查期间可能已退出：先停再排下一次，避免向已释放的令牌注册回调。
                if (ct.IsCancellationRequested) return;
                await Task.Delay(CheckInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出时取消定时检查属于正常路径。
        }
    }

    private static void PromptRestart(Window owner, UpdateInfo update)
    {
        if (_prompt is { IsVisible: true }) return;

        var window = new NoticeWindow { Title = "发现新版本" };
        window.SetNotice($"新版本 {update.TargetFullRelease.Version} 已下载", RestartMessage, null);
        window.PrimaryAction.Content = "立即重启";
        window.PrimaryAction.IsVisible = true;
        window.RestartRequested += () => ApplyAndRestart(update);
        window.Closed += (_, _) => _prompt = null;
        _prompt = window;

        // 定时检查常在窗口隐藏到托盘时触发；Avalonia 不允许以隐藏窗口为 owner 显示子窗。
        if (!owner.IsVisible) owner.Show();
        window.Show(owner);
    }

    private static void ApplyAndRestart(UpdateInfo update)
    {
        try
        {
            Manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Updater] 应用更新失败：{ex}");
        }
    }
}
