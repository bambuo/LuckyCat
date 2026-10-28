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
    private static string? _promptedVersion;
    private static UpdateInfo? _pendingUpdate;

    // forcePrompt：托盘手动检查时把已下载但被「稍后」关掉的提示重新弹出；定时检查仍按版本去重。
    public static async Task CheckNowAsync(Window owner, bool forcePrompt = false)
    {
        // 托盘手动检查与定时检查可能重叠：同一时刻只跑一份，避免重复下载与重复提示。
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;

        try
        {
            // IsInstalled 读取也放在 try 内：locator 缺失等初始化异常不能逸出成未观察任务。
            if (!Manager.IsInstalled) return;

            if (forcePrompt && _pendingUpdate is { } pending)
            {
                await Dispatcher.UIThread.InvokeAsync(() => PromptRestart(owner, pending, force: true));
                return;
            }

            var update = await Manager.CheckForUpdatesAsync();
            if (update is null) return;

            await Manager.DownloadUpdatesAsync(update);
            _pendingUpdate = update;
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
        try
        {
            if (!Manager.IsInstalled)
            {
                Console.WriteLine("[Updater] 当前不是 Velopack 安装版本，自动更新已停用。");
                return;
            }

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
        catch (Exception ex)
        {
            // 非取消异常不能拖死定时任务：留下日志后结束，避免更新检查静默永久停摆。
            Console.WriteLine($"[Updater] 定时检查停止：{ex.Message}");
        }
    }

    private static void PromptRestart(Window owner, UpdateInfo update, bool force = false)
    {
        var version = update.TargetFullRelease.Version.ToString();

        // 同一版本定时检查只提示一次；用户关掉提示后可从托盘「检查更新」强制重开。
        if (!force && version == _promptedVersion)
        {
            Console.WriteLine("[Updater] 该版本已提示过，跳过");
            return;
        }
        if (_prompt is { IsVisible: true } open)
        {
            open.Activate();
            return;
        }

        var window = new NoticeWindow { Title = "发现新版本" };
        window.SetNotice($"新版本 {update.TargetFullRelease.Version} 已下载", RestartMessage, null);
        window.PrimaryAction.Content = "立即重启";
        window.PrimaryAction.IsVisible = true;
        window.RestartRequested += () => ApplyAndRestart(update);
        window.Closed += (_, _) =>
        {
            // 只清自己的引用：旧窗口关闭不能把新窗口记的提示清掉。
            if (ReferenceEquals(_prompt, window)) _prompt = null;
        };
        _prompt = window;

        // 定时检查常在窗口隐藏到托盘或最小化时触发：Avalonia 不允许以不可见窗口为 owner，
        // 此时只显示提示窗本身，不把主窗口拉回前台。没有 owner 时 CenterOwner 会退化为平台默认位置，
        // 显式改为屏幕居中。
        var ownerVisible = owner.IsVisible && owner.WindowState != WindowState.Minimized;
        if (ownerVisible) window.Show(owner);
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }

        // Show 成功后才记录：外层 catch 只落日志，提前记录会让该版本在整个进程内被永久抑制。
        _promptedVersion = version;
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
