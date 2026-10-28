using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using LuckyCat.Contracts;

namespace LuckyCat.Hosting;

/// <summary>
/// 桌面模式的前端托管：wwwroot 下的 index.html 与实际资源由 Kestrel 直接服务，
/// 未匹配的非 /api 路径回退到 index.html 交给前端路由（深链刷新不 404）。
/// </summary>
public static class StaticFrontend
{
    public static void UseLuckyCatFrontend(WebApplication app, string webRoot)
    {
        if (!Directory.Exists(webRoot)) return;

        var provider = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
    }

    public static void MapLuckyCatFrontend(WebApplication app, string webRoot)
    {
        if (!Directory.Exists(webRoot)) return;

        var provider = new PhysicalFileProvider(webRoot);

        // 先注册 API 兜底：未匹配的 /api 路径返回 JSON 404，不被 index.html 吞掉。
        app.MapFallback("/api/{**rest}", () =>
            Results.Json(ApiResponse<object>.Fail("not_found", "接口不存在"), ApiJson.Options, statusCode: 404));

        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = provider });
    }
}
