using System.Text.Json;
using Avalonia.Controls;

namespace LuckyCat.Desktop;

public sealed class NavigationBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NativeWebView _web;

    public NavigationBridge(NativeWebView web)
    {
        _web = web;
        _web.WebMessageReceived += OnMessage;
    }

    public event Action<string>? RouteChanged;

    public Task NavigateAsync(string path) =>
        _web.InvokeScript($"window.__lcNavigate && window.__lcNavigate({JsonSerializer.Serialize(path)})");

    private void OnMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Body)) return;
        try
        {
            using var document = JsonDocument.Parse(e.Body);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "route"
                && root.TryGetProperty("path", out var path) && path.GetString() is { } value)
            {
                RouteChanged?.Invoke(value);
            }
        }
        catch (JsonException)
        {
            // 未知负载忽略：消息桥只服务标题栏选中态。
        }
    }
}
