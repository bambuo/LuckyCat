using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LuckyCat.Desktop.Core;

namespace LuckyCat.Desktop;

public partial class MainWindow : Window
{
    private readonly DesktopHost _host;
    private readonly NavigationBridge _bridge;

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

    private static void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        // GUI 进程无控制台；失败只记录，窗口本身不做提示层（原生层会盖住 Avalonia 控件）。
        if (!e.IsSuccess) Console.Error.WriteLine("WebView 导航未成功，请查看日志。");
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
