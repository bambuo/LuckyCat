using System.Net;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using LuckyCat.Desktop.Core;
using LuckyCat.Hosting;

namespace LuckyCat.Desktop;

public sealed class DesktopHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ActivationSignal _activation;
    private int _disposed;

    private DesktopHost(WebApplication app, Uri address, string dataDirectory, ActivationSignal activation)
    {
        _app = app;
        Address = address;
        DataDirectory = dataDirectory;
        _activation = activation;
    }

    public Uri Address { get; }

    public string DataDirectory { get; }

    // 第二个实例唤起主窗口：在 UI 线程触发，订阅方无需再调度。
    public event Action? ActivationRequested
    {
        add => _activation.Requested += value;
        remove => _activation.Requested -= value;
    }

    private sealed class ActivationSignal
    {
        public event Action? Requested;

        public void Raise() => Requested?.Invoke();
    }

    public static async Task<DesktopHost> StartAsync(string dataDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dataDirectory);

        var activation = new ActivationSignal();
        WebApplication? app = null;
        try
        {
            app = LuckyCatHost.Build([], LuckyCatHostOptions.Desktop(dataDirectory));

            // 二次启动唤起：仅桌面模式注册，只对回环来源生效。
            app.MapPost("/internal/activation", (HttpContext context) =>
            {
                if (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
                    return Results.NotFound();

                Dispatcher.UIThread.Post(activation.Raise);
                return Results.NoContent();
            });

            await app.StartAsync(ct);

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = new Uri(addresses?.FirstOrDefault()
                ?? throw new InvalidOperationException("Kestrel 未报告监听地址"));

            InstanceFile.Write(DesktopPaths.InstanceInfoPath(dataDirectory),
                new InstanceInfo(address.ToString(), Environment.ProcessId));

            return new DesktopHost(app, address, dataDirectory, activation);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            throw;
        }
    }

    public Task StopAsync(CancellationToken ct = default) => _app.StopAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // 停止超时也要继续释放，否则关窗后进程无法退出。
        }
        finally
        {
            await _app.DisposeAsync();
        }
    }
}
