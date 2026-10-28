using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LuckyCat.Desktop.Core;

namespace LuckyCat.Desktop;

public partial class App : Application
{
    // 第二实例无法唤起首实例时的提示文案；非空时只显示提示窗并退出，不启动宿主。
    public static string? StartupNotice { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        MacOSMenu.Install(ShowAbout, HideMainWindow, ExitApplication);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (StartupNotice is { } notice)
            {
                ShowStartupNotice(desktop, notice);
            }
            else
            {
                _ = StartAsync(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowAbout()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知";
        NoticeWindow.ShowMessage("关于招财猫",
            $"招财猫 {version}\n\n本机交易看板与 AI 交易员调度。数据与日志保存在用户数据目录，卸载应用不会删除。");
    }

    private void HideMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow main })
            main.HideToTray();
    }

    private void ExitApplication()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        // 主窗口负责停止管道与释放单实例锁；启动阶段（提示窗或启动窗）没有宿主，直接结束应用。
        if (desktop.MainWindow is MainWindow main) main.RequestExit();
        else desktop.Shutdown();
    }

    private static void ShowStartupNotice(IClassicDesktopStyleApplicationLifetime desktop, string notice)
    {
        var window = new NoticeWindow();
        desktop.MainWindow = window;
        window.SetNotice("招财猫已在运行", notice, ResolveLogDirectory());
        window.Closed += (_, _) => desktop.Shutdown();
        window.Show();
    }

    private static string? ResolveLogDirectory()
    {
        try
        {
            var dataDirectory = DesktopPaths.Resolve(
                Environment.GetEnvironmentVariable(DesktopPaths.DataDirEnvVar),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return DesktopPaths.LogDirectory(dataDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"解析日志目录失败：{ex}");
            return null;
        }
    }

    private static async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var cts = new CancellationTokenSource();
        SplashWindow? splash = null;
        void OnSplashClosed(object? sender, EventArgs e) => cts.Cancel();

        var opened = false;
        try
        {
            var dataDirectory = DesktopPaths.Resolve(
                Environment.GetEnvironmentVariable(DesktopPaths.DataDirEnvVar),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            // NativeWebView 是原生层，启动层必须独立成窗，不能覆盖在主窗口上。
            splash = new SplashWindow();
            splash.Closed += OnSplashClosed;
            desktop.MainWindow = splash;
            splash.Show();

            while (true)
            {
                DesktopHost host;
                try
                {
                    host = await DesktopHost.StartAsync(dataDirectory, cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"启动宿主失败：{ex}");
                    splash.ShowError(ex.Message, DesktopPaths.LogDirectory(dataDirectory));
                    if (!await splash.WaitForRetryAsync(cts.Token)) return;
                    continue;
                }

                if (cts.IsCancellationRequested)
                {
                    await host.DisposeAsync();
                    return;
                }

                var window = new MainWindow(host);
                desktop.MainWindow = window;
                window.Show();
                splash.Closed -= OnSplashClosed;
                splash.Close();
                opened = true;
                return;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动流程失败：{ex}");
        }
        finally
        {
            // 先退订再释放令牌：Shutdown 关闭启动窗时 Closed 仍会触发，不能撞上已释放的 CTS。
            if (splash is not null) splash.Closed -= OnSplashClosed;
            // 启动未完成（失败、取消或用户关闭启动窗口）：退出进程。
            if (!opened) desktop.Shutdown();
            cts.Dispose();
        }
    }
}
