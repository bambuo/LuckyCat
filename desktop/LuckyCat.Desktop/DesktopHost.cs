using System.Net;
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
    private readonly IDisposable _consoleRedirect;

    private DesktopHost(WebApplication app, Uri address, IDisposable consoleRedirect)
    {
        _app = app;
        Address = address;
        _consoleRedirect = consoleRedirect;
    }

    public Uri Address { get; }

    public static async Task<DesktopHost> StartAsync(string dataDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dataDirectory);
        var logDirectory = DesktopPaths.LogDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);

        // GUI 进程没有控制台：日志文件是启动失败时唯一的诊断通道。
        var consoleRedirect = ConsoleRedirect.ToFile(logDirectory, DateTimeOffset.Now);

        WebApplication? app = null;
        try
        {
            LogRetention.DeleteOlderThan(logDirectory, DateTimeOffset.Now, 7);

            app = LuckyCatHost.Build([], LuckyCatHostOptions.Desktop(dataDirectory));

            // 二次启动唤起：仅桌面模式注册，只对回环来源生效。
            app.MapPost("/internal/activation", (HttpContext context) =>
                context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
                    ? Results.NoContent()
                    : Results.NotFound());

            await app.StartAsync(ct);

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = new Uri(addresses?.FirstOrDefault()
                ?? throw new InvalidOperationException("Kestrel 未报告监听地址"));

            InstanceFile.Write(DesktopPaths.InstanceInfoPath(dataDirectory),
                new InstanceInfo(address.ToString(), Environment.ProcessId));

            return new DesktopHost(app, address, consoleRedirect);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            consoleRedirect.Dispose();
            throw;
        }
    }

    public Task StopAsync(CancellationToken ct = default) => _app.StopAsync(ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token);
        }
        finally
        {
            await _app.DisposeAsync();
            _consoleRedirect.Dispose();
        }
    }
}
