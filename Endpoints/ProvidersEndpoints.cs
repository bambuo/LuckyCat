using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;

namespace LuckyCat.Endpoints;

public static class ProvidersEndpoints
{
    public static RouteGroupBuilder MapProviders(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/providers");

        group.MapGet("/", async (AppDbContext db) =>
        {
            var items = await db.Providers.OrderByDescending(p => p.Id).ToListAsync();
            return Results.Ok(items.Select(ToDto));
        });

        group.MapPost("/", async (ProviderUpsertRequest request, AppDbContext db) =>
        {
            var error = Validate(request);
            if (error is not null) return Results.BadRequest(new { message = error });

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
            return Results.Created($"/api/providers/{provider.Id}", ToDto(provider));
        });

        group.MapPut("/{id:int}", async (int id, ProviderUpsertRequest request, AppDbContext db) =>
        {
            var provider = await db.Providers.FindAsync(new object[] { id });
            if (provider is null) return Results.NotFound();

            var error = Validate(request);
            if (error is not null) return Results.BadRequest(new { message = error });

            provider.Name = request.Name.Trim();
            provider.Type = request.Type;
            provider.BaseUrl = request.BaseUrl.Trim().TrimEnd('/');
            provider.Model = request.Model.Trim();
            provider.ApiVersion = string.IsNullOrWhiteSpace(request.ApiVersion) ? null : request.ApiVersion.Trim();
            provider.ThinkingMode = NormalizeThinkingMode(request.ThinkingMode);
            if (request.ContextTokens is > 0) provider.ContextTokens = request.ContextTokens.Value;
            if (request.MaxOutputTokens is >= 0) provider.MaxOutputTokens = request.MaxOutputTokens.Value;
            provider.AutoCompressContext = request.AutoCompressContext ?? false;
            // ApiKey 留空 = 保持原值
            if (!string.IsNullOrWhiteSpace(request.ApiKey)) provider.ApiKey = request.ApiKey.Trim();
            provider.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(ToDto(provider));
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var provider = await db.Providers.FindAsync(new object[] { id });
            if (provider is null) return Results.NotFound();

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
            if (provider is null) return Results.NotFound();

            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            await foreach (var evt in runner.RunAsync(provider, ct))
            {
                var json = JsonSerializer.Serialize(evt, VerificationJson.Options);
                await http.Response.WriteAsync($"data: {json}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            return Results.Empty;
        });

        return group;
    }

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
        p.CreatedAt, p.UpdatedAt);
}
