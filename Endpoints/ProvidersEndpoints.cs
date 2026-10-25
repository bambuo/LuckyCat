using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;

namespace LuckyCat.Endpoints;

public static class ProvidersEndpoints
{
    extension(IEndpointRouteBuilder app)
    {
        public RouteGroupBuilder MapProviders()
        {
            var group = app.MapGroup("/api/providers");

            group.MapGet("/", async (AppDbContext db) =>
            {
                var items = await db.Providers.OrderByDescending(p => p.Id).ToListAsync();
                return Ok(items.Select(ToDto).ToList());
            });

            group.MapPost("/", async (ProviderUpsertRequest request, AppDbContext db) =>
            {
                var error = Validate(request);
                if (error is not null) return Fail("validation_failed", error, 400);

                var now = DateTime.UtcNow;
                var provider = new Provider
                {
                    Name = request.Name.Trim(),
                    Type = request.Type,
                    BaseUrl = request.BaseUrl.Trim().TrimEnd('/'),
                    ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim(),
                    Model = request.Model.Trim(),
                    ApiVersion = string.IsNullOrWhiteSpace(request.ApiVersion) ? null : request.ApiVersion.Trim(),
                    ThinkingMode = NormalizeThinkingMode(request.ThinkingMode),
                    ContextTokens = request.ContextTokens is > 0 ? request.ContextTokens.Value : 128000,
                    MaxOutputTokens = request.MaxOutputTokens is >= 0 ? request.MaxOutputTokens.Value : 16384,
                    AutoCompressContext = request.AutoCompressContext ?? false,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Providers.Add(provider);
                await db.SaveChangesAsync();
                return Created(ToDto(provider));
            });

            group.MapPut("/{id:int}", async (int id, ProviderUpsertRequest request, AppDbContext db) =>
            {
                var provider = await db.Providers.FindAsync(new object[] { id });
                if (provider is null) return Fail("not_found", "提供商不存在", 404);

                var error = Validate(request);
                if (error is not null) return Fail("validation_failed", error, 400);

                // 连接身份（type/baseUrl/model/apiKey）任一变化都必须重置工具能力验证字段，
                // 要求重新验证后才能 start/test-run（LC-STD-03 §2.2、API-09）。
                var nextBaseUrl = request.BaseUrl.Trim().TrimEnd('/');
                var nextApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? provider.ApiKey : request.ApiKey.Trim();
                var connectionChanged = provider.Type != request.Type
                    || !string.Equals(provider.BaseUrl, nextBaseUrl, StringComparison.Ordinal)
                    || !string.Equals(provider.Model, request.Model.Trim(), StringComparison.Ordinal)
                    || !string.Equals(provider.ApiKey ?? "", nextApiKey ?? "", StringComparison.Ordinal);

                provider.Name = request.Name.Trim();
                provider.Type = request.Type;
                provider.BaseUrl = nextBaseUrl;
                provider.Model = request.Model.Trim();
                provider.ApiVersion = string.IsNullOrWhiteSpace(request.ApiVersion) ? null : request.ApiVersion.Trim();
                provider.ThinkingMode = NormalizeThinkingMode(request.ThinkingMode);
                if (request.ContextTokens is > 0) provider.ContextTokens = request.ContextTokens.Value;
                if (request.MaxOutputTokens is >= 0) provider.MaxOutputTokens = request.MaxOutputTokens.Value;
                provider.AutoCompressContext = request.AutoCompressContext ?? false;
                // ApiKey 留空 = 保持原值
                if (!string.IsNullOrWhiteSpace(request.ApiKey)) provider.ApiKey = request.ApiKey.Trim();
                if (connectionChanged)
                {
                    provider.ToolCallingSupported = null;
                    provider.ToolCallingVerifiedAt = null;
                }
                provider.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync();
                return Ok(ToDto(provider));
            });

            group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
            {
                var provider = await db.Providers.FindAsync(new object[] { id });
                if (provider is null) return Fail("not_found", "提供商不存在", 404);

                // 提供商仅在无交易员引用时可删除，不做级联（LC-STD-03 §1.4）
                if (await db.Traders.AnyAsync(t => t.ProviderId == id))
                {
                    return Fail("resource_in_use", "该提供商仍被交易员引用，请先解除绑定", 409);
                }

                db.Providers.Remove(provider);
                await db.SaveChangesAsync();
                return Results.NoContent();
            });

            group.MapPost("/{id:int}/verify", async (
                int id,
                AppDbContext db,
                VerificationRunner runner,
                HttpContext http,
                CancellationToken ct) =>
            {
                var provider = await db.Providers.FindAsync(new object[] { id }, ct);
                if (provider is null) return Fail("not_found", "提供商不存在", 404);

                http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
                http.Response.Headers.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";

                await foreach (var evt in runner.RunAsync(provider, ct))
                {
                    // 工具检查结果必须回写能力字段（LC-STD-03 §3.1）；结果一到就落库，
                    // 后续检查项或连接中断不会丢失已验证事实。
                    if (evt is { Type: "result", Check: VerificationRunner.CheckTools })
                    {
                        provider.ToolCallingSupported = evt.Status == "pass";
                        provider.ToolCallingVerifiedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }

                    var json = JsonSerializer.Serialize(evt, VerificationJson.Options);
                    await http.Response.WriteAsync($"data: {json}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }

                return Results.Empty;
            });

            return group;
        }
    }

    private static IResult Ok<T>(T data) => ApiResults.Ok(data);

    private static IResult Created<T>(T data) => ApiResults.Created(data);

    private static IResult Fail(string code, string message, int statusCode) =>
        ApiResults.Fail(code, message, statusCode);

    private static string? Validate(ProviderUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "名称不能为空";
        if (string.IsNullOrWhiteSpace(request.BaseUrl)) return "BaseUrl 不能为空";
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out _)) return "BaseUrl 不是合法的绝对 URL";
        if (string.IsNullOrWhiteSpace(request.Model)) return "模型名不能为空";
        return null;
    }

    private static string NormalizeThinkingMode(string? mode) =>
        mode is "on" or "off" ? mode : "default";

    private static ProviderDto ToDto(Provider p) => new(
        p.Id, p.Name, p.Type, p.BaseUrl, ApiKeyMasker.Mask(p.ApiKey), p.Model, p.ApiVersion,
        p.ThinkingMode, p.ContextTokens, p.MaxOutputTokens, p.AutoCompressContext,
        p.ToolCallingSupported, p.ToolCallingVerifiedAt,
        p.CreatedAt, p.UpdatedAt);
}
