using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LuckyCat.Desktop.Core;

namespace LuckyCat.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
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
