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

    // 脚本桥不可用（页面未加载完或注入失败）时返回 false，由调用方回退整页导航。
    public async Task<bool> NavigateAsync(string path)
    {
        try
        {
            await _web.InvokeScript($"window.__lcNavigate && window.__lcNavigate({JsonSerializer.Serialize(path)})");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"标题栏导航失败（{path}）：{ex}");
            return false;
        }
    }

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
