using Avalonia;
using LuckyCat.Desktop.Core;
using Velopack;

namespace LuckyCat.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 钩子必须最早执行：安装/更新回调依赖它。
        VelopackApp.Build().Run();

        // GUI 进程没有控制台：在 Avalonia 启动前把 stdout/stderr 落到数据目录，日志归属入口进程。
        // 前置步骤失败不能阻止启动：否则启动界面还没出现进程就退出，用户看不到任何错误。
        string? dataDirectory = null;
        try
        {
            dataDirectory = DesktopPaths.Resolve(
                Environment.GetEnvironmentVariable(DesktopPaths.DataDirEnvVar),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Directory.CreateDirectory(dataDirectory);
            var logDirectory = DesktopPaths.LogDirectory(dataDirectory);
            Directory.CreateDirectory(logDirectory);

            // 先重定向再清理：清理失败不能导致整场会话没有日志。
            ConsoleRedirect.ToFile(logDirectory, DateTimeOffset.Now);
            try
            {
                LogRetention.DeleteOlderThan(logDirectory, DateTimeOffset.Now, 7);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"清理过期日志失败，继续启动：{ex}");
            }
        }
        catch (Exception ex)
        {
            // 未重定向时落到 stderr；启动继续，错误由启动窗展示。
            Console.Error.WriteLine($"启动前置步骤失败，继续以无日志方式启动：{ex}");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"未处理异常：{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"未观察的任务异常：{e.Exception}");
            e.SetObserved();
        };

        // 数据目录不可用时跳过单实例判断：拿不到锁文件也不该把正常启动挡在门外。
        using var instanceLock = dataDirectory is null
            ? null
            : InstanceLock.TryAcquire(DesktopPaths.InstanceLockPath(dataDirectory));

        if (instanceLock is null && dataDirectory is not null)
        {
            // 第二个实例：唤起成功就直接退出，不再初始化 Avalonia（不产生第二份宿主）。
            if (!TryActivatePrimary(dataDirectory))
            {
                App.StartupNotice = "已有实例正在运行，但无法唤起其窗口。请从托盘中打开。";
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool TryActivatePrimary(string dataDirectory)
    {
        var info = InstanceFile.Read(DesktopPaths.InstanceInfoPath(dataDirectory));
        if (info is null) return false;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        return InstanceFile.TryActivateAsync(info, client).GetAwaiter().GetResult();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
