using Avalonia.Controls;
using LuckyCat.Desktop.Core;

namespace LuckyCat.Desktop;

public partial class MainWindow : Window
{
    private readonly string _dataDir = DesktopPaths.Resolve(
        Environment.GetEnvironmentVariable(DesktopPaths.DataDirEnvVar),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private DesktopHost? _host;
    private NavigationBridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        Bar.SelectionChanged += path => _ = _bridge?.NavigateAsync(path);
        Web.NavigationCompleted += OnNavigationCompleted;
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            _host = await DesktopHost.StartAsync(_dataDir);
            _bridge = new NavigationBridge(Web);
            _bridge.RouteChanged += path =>
            {
                if (NavigationItems.Match(path) is { } item) Bar.Select(item.Path);
            };
            Web.Source = new Uri($"{_host.Address}traders?desktop=1");
        }
        catch (Exception ex)
        {
            SplashTitle.Text = "启动失败";
            SplashDetail.Text = $"{ex.Message}\n日志目录：{DesktopPaths.LogDirectory(_dataDir)}";
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) Splash.IsVisible = false;
    }
}
