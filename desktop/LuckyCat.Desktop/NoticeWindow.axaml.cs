using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyCat.Desktop;

public partial class NoticeWindow : Window
{
    private string? _logDirectory;

    public NoticeWindow() => InitializeComponent();

    public void SetNotice(string title, string message, string logDirectory)
    {
        _logDirectory = logDirectory;
        NoticeTitle.Text = title;
        NoticeBody.Text = message;
    }

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
            NoticeBody.Text = $"无法打开日志目录：{ex.Message}";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
