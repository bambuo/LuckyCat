using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using LuckyCat.Hosting;
using Xunit;

namespace LuckyCat.Tests;

public class StaticFrontendTests
{
    [Fact]
    public async Task Serves_index_assets_and_api_404_without_shadowing_html()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "luckycat-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>LuckyCat</title>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "app.js"), "console.log('ok')");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        StaticFrontend.UseLuckyCatFrontend(app, webRoot);
        app.MapGet("/api/health", () => Results.Json(new { success = true }));
        StaticFrontend.MapLuckyCatFrontend(app, webRoot);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            var root = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Contains("LuckyCat", await root.Content.ReadAsStringAsync());

            var deepLink = await client.GetAsync("/cycles");
            Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
            Assert.Contains("LuckyCat", await deepLink.Content.ReadAsStringAsync());

            var asset = await client.GetAsync("/assets/app.js");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Contains("console.log", await asset.Content.ReadAsStringAsync());

            var health = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var unknownApi = await client.GetAsync("/api/unknown");
            Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
            Assert.Equal("application/json", unknownApi.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await unknownApi.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("not_found", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Host_build_with_options_and_static_files_disabled_serves_health()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "luckycat-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var options = new LuckyCatHostOptions(
            ContentRootPath: RepositoryRoot(),
            DataDirectory: dataDirectory,
            Urls: "http://127.0.0.1:0",
            ServeStaticFiles: false);

        // 独立临时库：启动恢复/净值同步/复盘调度都查不到交易员与账户，不会发起真实网络调用。
        var app = LuckyCatHost.Build([], options);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var response = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LuckyCat.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("未找到仓库根目录（LuckyCat.csproj）");
    }
}
