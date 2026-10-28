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
        var dataDirectory = DesktopPaths.Resolve(
            Environment.GetEnvironmentVariable(DesktopPaths.DataDirEnvVar),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        // NativeWebView 是原生层，启动层必须独立成窗，不能覆盖在主窗口上。
        var splash = new SplashWindow();
        var cts = new CancellationTokenSource();
        void OnSplashClosed(object? sender, EventArgs e) => cts.Cancel();

        splash.Closed += OnSplashClosed;
        desktop.MainWindow = splash;
        splash.Show();

        var opened = false;
        try
        {
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
            // 启动未完成（失败、取消或用户关闭启动窗口）：退出进程。
            if (!opened) desktop.Shutdown();
            cts.Dispose();
        }
    }
}
