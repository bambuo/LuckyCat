using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LuckyCat.Desktop.Core;

namespace LuckyCat.Desktop;

public partial class MainWindow : Window
{
    // 无显示器环境无法验证托盘交互时，用该变量强制走「无托盘」关闭路径。
    private const string DisableTrayEnvVar = "LUCKYCAT_DISABLE_TRAY";
    private static readonly TimeSpan SuppressedNavigationWindow = TimeSpan.FromSeconds(5);

    private readonly DesktopHost _host;
    private readonly NavigationBridge _bridge;
    private readonly string _dataDir;
    private NoticeWindow? _notice;
    private TrayController? _tray;
    private readonly CancellationTokenSource _updateCts = new();
    private bool _exiting;
    private int _shutdownStarted;

    // 被取消的外链导航在部分平台仍会以 IsSuccess=false 回调；记录目标以抑制误报。
    private Uri? _suppressedNavigationTarget;
    private DateTimeOffset _suppressedNavigationAt;

    // XAML 编译器要求公共无参构造才认为资源可由运行时加载器实例化；应用只通过带宿主的重载创建窗口。
    public MainWindow() =>
        throw new InvalidOperationException("请使用 MainWindow(DesktopHost) 创建主窗口。");

    public MainWindow(DesktopHost host)
    {
        _host = host;
        _dataDir = host.DataDirectory;

        InitializeComponent();

        _bridge = new NavigationBridge(Web);
        _bridge.RouteChanged += path =>
        {
            if (NavigationItems.Match(path) is { } item) Bar.Select(item.Path);
        };
        Bar.SelectionChanged += async path =>
        {
            if (await _bridge.NavigateAsync(path)) return;

            // 脚本桥不可用时退回整页导航：标题栏点击不能静默失败。
            try
            {
                Web.Source = new Uri(_host.Address, path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"标题栏回退导航失败（{path}）：{ex}");
            }
        };
        Web.NavigationCompleted += OnNavigationCompleted;
        Web.NavigationStarted += OnNavigationStarted;
        Web.NewWindowRequested += OnNewWindowRequested;

        Bar.Select(NavigationItems.All[0].Path);
        Web.Source = new Uri($"{host.Address}traders?desktop=1");

        _host.ActivationRequested += OnActivationRequested;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    public string DataDirectory => _dataDir;

    private bool TrayAvailable => _tray is not null;

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;

        // 宿主已就绪：托盘是否可用不影响自动更新检查。
        _ = Updater.StartPeriodic(this, _updateCts.Token);

        try
        {
            if (Environment.GetEnvironmentVariable(DisableTrayEnvVar) == "1")
            {
                Console.WriteLine($"托盘已按 {DisableTrayEnvVar} 禁用：关闭窗口将直接退出。");
                return;
            }

            var linux = OperatingSystem.IsLinux();
            var statusNotifier = !linux || await Task.Run(StatusNotifierProbe.Available);
            if (!TrayProbe.ShouldUseTray(linux, statusNotifier))
            {
                Console.WriteLine("托盘不可用：当前 Linux 会话未提供 StatusNotifier，关闭窗口将直接退出。");
                return;
            }

            // 探测期间用户可能已经关窗，此时不再创建托盘。
            if (_exiting) return;

            _tray = TrayController.Create(this, _host.Address, OpenDataFolder, RequestUpdateCheck, ExitApplication);
            Console.WriteLine(_tray is null
                ? "托盘创建失败：关闭窗口将直接退出。"
                : $"托盘已创建：关闭窗口将最小化到托盘（服务地址 {_host.Address}）。");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"初始化托盘失败：{ex}");
        }
    }

    private void OnActivationRequested()
    {
        if (_exiting) return;

        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting) return;

        // 系统关机或应用级退出不能被拦成「隐藏到托盘」。
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            _exiting = true;
            _ = ShutdownAsync();
            return;
        }

        if (!TrayAvailable)
        {
            // Linux 无 StatusNotifier：关窗即退出，避免进程隐身。
            _exiting = true;
            _ = ShutdownAsync();
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void ShowCloseHintOnce()
    {
        var path = DesktopPaths.SettingsPath(_dataDir);
        if (CloseHintShown(path)) return;

        try
        {
            Directory.CreateDirectory(_dataDir);
            var settings = ReadSettings(path);
            settings["closeToTrayHintShown"] = true;
            File.WriteAllText(path, settings.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记录失败不影响本次提示，但下次关窗会再提示。
            Console.Error.WriteLine($"写入桌面设置失败：{ex}");
        }

        // macOS 上 Avalonia 的 TrayIcon.Clicked 不触发，只能引导用户走菜单。
        var message = OperatingSystem.IsMacOS()
            ? "交易继续运行。从菜单栏托盘图标菜单中选择「打开主窗口」可恢复。"
            : "交易继续运行。点击托盘图标可恢复窗口，托盘菜单可退出。";
        NoticeWindow.ShowMessage("已最小化到托盘", message);
    }

    private static bool CloseHintShown(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("closeToTrayHintShown", out var shown)
                   && shown.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static JsonObject ReadSettings(string path)
    {
        if (!File.Exists(path)) return new JsonObject();

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // 设置文件损坏时直接重建，不能因此丢掉首次提示。
            return new JsonObject();
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_dataDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"打开数据目录失败：{ex}");
        }
    }

    private void RequestUpdateCheck() => _ = Updater.CheckNowAsync(this, forcePrompt: true);

    private void ExitApplication()
    {
        _exiting = true;
        _ = ShutdownAsync();
    }

    // macOS 菜单「隐藏招财猫」与 ⌘W：托盘可用时只隐藏；没有托盘就没有恢复入口，按关窗路径退出。
    internal void HideToTray()
    {
        if (_exiting) return;

        if (!TrayAvailable)
        {
            ExitApplication();
            return;
        }

        Hide();
        ShowCloseHintOnce();
    }

    internal void RequestExit() => ExitApplication();

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1) return;

        // 先停定时检查，再释放宿主，避免检查在退出过程中继续挂起。
        _updateCts.Cancel();

        try
        {
            // WebView 还持有 /api/traders/*/stream 的 SSE 长连接，Kestrel 要等它结束才停止。
            // 先把 WebView 导到空白页断开连接，限时等待后释放宿主，避免退出卡约 5 秒。
            Web.Source = new Uri("about:blank");
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"退出前断开 WebView 失败：{ex}");
        }

        try
        {
            await _host.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"释放桌面宿主失败：{ex}");
        }

        _tray?.Dispose();
        _tray = null;

        _updateCts.Dispose();

        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) return;

        var requested = e.Request ?? Web.Source;
        if (IsSuppressedNavigation(requested)) return;

        // 原生 WebView 覆盖内容区，失败提示只能独立成窗；主窗口保持打开以便重试导航。
        var logDirectory = DesktopPaths.LogDirectory(_host.DataDirectory);
        var target = requested?.ToString() ?? "未知地址";
        var message =
            $"目标地址：{target}\n" +
            "错误信息：WebView 报告导航未完成（IsSuccess=False）\n" +
            $"日志目录：{logDirectory}";

        Console.Error.WriteLine($"WebView 导航失败：{target}");

        if (_notice is { IsVisible: true } open)
        {
            open.SetNotice("页面加载失败", message, logDirectory);
            open.Activate();
            return;
        }

        _notice = new NoticeWindow();
        _notice.Closed += (_, _) => _notice = null;
        _notice.SetNotice("页面加载失败", message, logDirectory);
        _notice.Show(this);
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        // target="_blank" 不能在应用内开新窗口：取消并交给系统浏览器。
        e.Handled = true;
        OpenInSystemBrowser(e.Request);
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is not { } target || IsAppAddress(target)) return;

        // 应用窗口只加载本机页面；外链一律取消并交给系统浏览器。
        // 取消后 WebView 仍可能回调 NavigationCompleted(IsSuccess=false)，记录目标用于抑制误报。
        _suppressedNavigationTarget = target;
        _suppressedNavigationAt = DateTimeOffset.UtcNow;
        e.Cancel = true;
        OpenInSystemBrowser(target);
    }

    private bool IsSuppressedNavigation(Uri? target)
    {
        if (_suppressedNavigationTarget is not { } suppressed) return false;

        if (DateTimeOffset.UtcNow - _suppressedNavigationAt > SuppressedNavigationWindow)
        {
            _suppressedNavigationTarget = null;
            return false;
        }

        // 部分平台不回填 Request：只能按时间窗口认定这是被取消的那次导航。
        if (target is not null && !SameNavigationTarget(target, suppressed)) return false;

        _suppressedNavigationTarget = null;
        return true;
    }

    private static bool SameNavigationTarget(Uri left, Uri right) =>
        left.IsAbsoluteUri && right.IsAbsoluteUri
            ? string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
            : left.Equals(right);

    private static bool IsAppAddress(Uri uri) =>
        uri.Scheme == "about"
        || ((uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.IsLoopback);

    private static void OpenInSystemBrowser(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme == "about") return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"打开外部链接失败（{uri}）：{ex}");
        }
    }
}
