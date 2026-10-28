using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LuckyCat.Desktop;

public partial class SplashWindow : Window
{
    private string? _logDirectory;

    public SplashWindow() => InitializeComponent();

    public event Action? RetryRequested;

    public void ShowError(string message, string logDirectory)
    {
        _logDirectory = logDirectory;
        StatusTitle.Text = "启动失败";
        StatusDetail.Text = $"{message}\n日志目录：{logDirectory}";
        Actions.IsVisible = true;
    }

    public void ShowProgress()
    {
        _logDirectory = null;
        StatusTitle.Text = "正在启动招财猫…";
        StatusDetail.Text = string.Empty;
        Actions.IsVisible = false;
    }

    public async Task<bool> WaitForRetryAsync(CancellationToken ct)
    {
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRetryRequested() => retry.TrySetResult();

        RetryRequested += OnRetryRequested;
        try
        {
            await retry.Task.WaitAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            RetryRequested -= OnRetryRequested;
        }
    }

    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        ShowProgress();
        RetryRequested?.Invoke();
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
            StatusDetail.Text = $"无法打开日志目录：{ex.Message}";
        }
    }
}
