using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LuckyCat.Desktop.Core;

namespace LuckyCat.Desktop;

public partial class MainWindow : Window
{
    private readonly DesktopHost _host;
    private readonly NavigationBridge _bridge;
    private NoticeWindow? _notice;

    // XAML 编译器要求公共无参构造才认为资源可由运行时加载器实例化；应用只通过带宿主的重载创建窗口。
    public MainWindow() =>
        throw new InvalidOperationException("请使用 MainWindow(DesktopHost) 创建主窗口。");

    public MainWindow(DesktopHost host)
    {
        _host = host;
        InitializeComponent();

        _bridge = new NavigationBridge(Web);
        _bridge.RouteChanged += path =>
        {
            if (NavigationItems.Match(path) is { } item) Bar.Select(item.Path);
        };
        Bar.SelectionChanged += path => _ = _bridge.NavigateAsync(path);
        Web.NavigationCompleted += OnNavigationCompleted;

        Bar.Select(NavigationItems.All[0].Path);
        Web.Source = new Uri($"{host.Address}traders?desktop=1");
        Closed += OnClosed;
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) return;

        // 原生 WebView 覆盖内容区，失败提示只能独立成窗；主窗口保持打开以便重试导航。
        var logDirectory = DesktopPaths.LogDirectory(_host.DataDirectory);
        var target = e.Request?.ToString() ?? Web.Source?.ToString() ?? "未知地址";
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

    private async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            await _host.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"释放桌面宿主失败：{ex}");
        }
        finally
        {
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }
}
