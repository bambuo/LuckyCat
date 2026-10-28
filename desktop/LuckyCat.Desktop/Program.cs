using Avalonia;
using LuckyCat.Desktop.Core;
using Velopack;

namespace LuckyCat.Desktop;

internal static class Program
{
    // 首实例可能尚未写完 instance.json；二次启动按 1 秒间隔重试，避免启动期误报。
    private const int ActivationAttempts = 10;
    private static readonly TimeSpan ActivationRetryDelay = TimeSpan.FromSeconds(1);

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
        // 目录不可写与「已被占用」必须区分：前者无法运行，后者是二次启动。
        InstanceLock? instanceLock;
        try
        {
            instanceLock = dataDirectory is null
                ? null
                : InstanceLock.TryAcquire(DesktopPaths.InstanceLockPath(dataDirectory));
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"数据目录不可写：{ex}");
            App.StartupNotice =
                $"无法写入数据目录：{dataDirectory}\n请检查目录权限，或通过 {DesktopPaths.DataDirEnvVar} 指定可写目录。";
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        using (instanceLock)
        {
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
    }

    private static bool TryActivatePrimary(string dataDirectory)
    {
        var path = DesktopPaths.InstanceInfoPath(dataDirectory);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        for (var attempt = 1; attempt <= ActivationAttempts; attempt++)
        {
            if (attempt > 1) Thread.Sleep(ActivationRetryDelay);

            var info = InstanceFile.Read(path);
            if (info is not null && InstanceFile.TryActivateAsync(info, client).GetAwaiter().GetResult())
                return true;
        }

        Console.Error.WriteLine($"已重试 {ActivationAttempts} 次仍无法唤起首实例窗口。");
        return false;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // macOS 的应用菜单由 MacOSMenu 自建，关掉 Avalonia 注入的英文默认项，避免重复的 Quit/Hide。
        if (OperatingSystem.IsMacOS())
            builder.With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true });

        return builder;
    }
}
