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
        var dataDirectory = DesktopPaths.Resolve(
            Environment.GetEnvironmentVariable(DesktopPaths.DataDirEnvVar),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Directory.CreateDirectory(dataDirectory);
        var logDirectory = DesktopPaths.LogDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);
        LogRetention.DeleteOlderThan(logDirectory, DateTimeOffset.Now, 7);
        ConsoleRedirect.ToFile(logDirectory, DateTimeOffset.Now);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"未处理异常：{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"未观察的任务异常：{e.Exception}");
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
