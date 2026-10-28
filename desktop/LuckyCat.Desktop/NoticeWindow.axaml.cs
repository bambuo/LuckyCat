using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyCat.Desktop;

public partial class NoticeWindow : Window
{
    private string? _logDirectory;

    public NoticeWindow() => InitializeComponent();

    public event Action? RestartRequested;

    public static void ShowMessage(string title, string message)
    {
        var window = new NoticeWindow();
        window.SetNotice(title, message, null);
        window.Show();
    }

    public void SetNotice(string title, string message, string? logDirectory)
    {
        _logDirectory = logDirectory;
        Heading.Text = title;
        Body.Text = message;
        LogButton.IsVisible = logDirectory is not null;
    }

    private void OnPrimary(object? sender, RoutedEventArgs e) => RestartRequested?.Invoke();

    private void OnOpenLogs(object? sender, RoutedEventArgs e)
    {
        if (_logDirectory is null) return;

        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Body.Text = $"无法打开日志目录：{ex.Message}";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
