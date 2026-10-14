using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;

namespace LuckyCat.Endpoints;

public static class ExchangesEndpoints
{
    public static RouteGroupBuilder MapExchanges(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/exchanges");

        group.MapGet("/", async (AppDbContext db) =>
        {
            var items = await db.ExchangeAccounts.OrderByDescending(e => e.Id).ToListAsync();
            return Results.Ok(items.Select(ToDto));
        });

        group.MapGet("/outbound-ip", async (IHttpClientFactory httpClientFactory) =>
        {
            var ip = await ResolveOutboundIpAsync(httpClientFactory);
            return Results.Ok(new OutboundIpDto(ip));
        });

        // 真实读取账户 USDT 余额（交易员「获取当前余额」与看板用；读取失败如实返回错误）
        group.MapGet("/{id:int}/balance", async (int id, AppDbContext db, ExchangeTradingService trading, CancellationToken ct) =>
        {
            var account = await db.ExchangeAccounts.FindAsync(new object[] { id }, ct);
            if (account is null) return Results.NotFound();

            var snapshot = await trading.GetAccountAsync(account, ct);
            return snapshot.Success
                ? Results.Ok(new { usdt = snapshot.UsdtEquity, available = snapshot.UsdtAvailable })
                : Results.BadRequest(new { message = snapshot.Error ?? "余额查询失败" });
        });

        group.MapPost("/", async (ExchangeUpsertRequest request, AppDbContext db) =>
        {
            var error = Validate(request, isCreate: true, existingPassphrase: null);
            if (error is not null) return Results.BadRequest(new { message = error });

            var now = DateTime.UtcNow;
            var account = new ExchangeAccount
            {
                ExchangeType = request.ExchangeType,
                AccountName = request.AccountName.Trim(),
                ApiKey = request.ApiKey!.Trim(),
                ApiSecret = request.ApiSecret!.Trim(),
                Passphrase = RequiresPassphrase(request.ExchangeType) && !string.IsNullOrWhiteSpace(request.Passphrase)
                    ? request.Passphrase.Trim()
                    : null,
                UserId = NormalizeUserId(request),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ExchangeAccounts.Add(account);
            await db.SaveChangesAsync();
            return Results.Created($"/api/exchanges/{account.Id}", ToDto(account));
        });

        group.MapPut("/{id:int}", async (int id, ExchangeUpsertRequest request, AppDbContext db) =>
        {
            var account = await db.ExchangeAccounts.FindAsync(new object[] { id });
            if (account is null) return Results.NotFound();

            // 切换交易所类型时旧类型的口令不可沿用
            var switching = request.ExchangeType != account.ExchangeType;
            var error = Validate(request, isCreate: false, existingPassphrase: switching ? null : account.Passphrase);
            if (error is not null) return Results.BadRequest(new { message = error });

            account.ExchangeType = request.ExchangeType;
            account.AccountName = request.AccountName.Trim();
            if (!string.IsNullOrWhiteSpace(request.ApiKey)) account.ApiKey = request.ApiKey.Trim();
            if (!string.IsNullOrWhiteSpace(request.ApiSecret)) account.ApiSecret = request.ApiSecret.Trim();
            if (!RequiresPassphrase(request.ExchangeType)) account.Passphrase = null;
            else if (!string.IsNullOrWhiteSpace(request.Passphrase)) account.Passphrase = request.Passphrase.Trim();
            // UID 在表单中始终可见：按提交值原样生效（清空即清除；非 Gate.io 恒为空）
            account.UserId = NormalizeUserId(request);
            account.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(ToDto(account));
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var account = await db.ExchangeAccounts.FindAsync(new object[] { id });
            if (account is null) return Results.NotFound();

            db.ExchangeAccounts.Remove(account);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/verify", async (
            int id,
            AppDbContext db,
            ExchangeVerificationRunner runner,
            HttpContext http,
            CancellationToken ct) =>
        {
            var account = await db.ExchangeAccounts.FindAsync(new object[] { id }, ct);
            if (account is null) return Results.NotFound();

            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            await foreach (var evt in runner.RunAsync(account, ct))
            {
                var json = JsonSerializer.Serialize(evt, VerificationJson.Options);
                await http.Response.WriteAsync($"data: {json}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            return Results.Empty;
        });

        return group;
    }

    private static string? Validate(ExchangeUpsertRequest request, bool isCreate, string? existingPassphrase)
    {
        if (string.IsNullOrWhiteSpace(request.AccountName)) return "账户名称不能为空";

        if (isCreate)
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey)) return "API Key 不能为空";
            if (string.IsNullOrWhiteSpace(request.ApiSecret)) return "API Secret 不能为空";
        }

        if (RequiresPassphrase(request.ExchangeType))
        {
            var effective = string.IsNullOrWhiteSpace(request.Passphrase) ? existingPassphrase : request.Passphrase;
            if (string.IsNullOrWhiteSpace(effective)) return "该交易所需要口令（Passphrase）";
        }

        if (request.ExchangeType == ExchangeType.GateIo && !string.IsNullOrWhiteSpace(request.UserId)
            && !request.UserId.Trim().All(char.IsAsciiDigit))
        {
            return "Gate.io 用户 ID（UID）应为纯数字";
        }

        return null;
    }

    private static bool RequiresPassphrase(ExchangeType type) =>
        type is ExchangeType.OKX or ExchangeType.Bitget or ExchangeType.Kucoin;

    // Gate.io 的 UID 才有意义；其余交易所（含切换类型后）置空
    private static string? NormalizeUserId(ExchangeUpsertRequest request) =>
        request.ExchangeType == ExchangeType.GateIo && !string.IsNullOrWhiteSpace(request.UserId)
            ? request.UserId.Trim()
            : null;

    private static readonly string[] OutboundIpSources =
    [
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
    ];

    // 出口 IP 即交易所看到的来源 IP：由后端进程实时查询，失败时依次尝试备选源
    private static async Task<string?> ResolveOutboundIpAsync(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        foreach (var url in OutboundIpSources)
        {
            try
            {
                var text = (await client.GetStringAsync(url)).Trim();
                if (IPAddress.TryParse(text, out _)) return text;
            }
            catch
            {
                // 忽略单个源的失败，尝试下一个
            }
        }
        return null;
    }

    private static ExchangeAccountDto ToDto(ExchangeAccount e) => new(
        e.Id,
        e.ExchangeType,
        e.AccountName,
        ApiKeyMasker.Mask(e.ApiKey),
        ApiKeyMasker.Mask(e.ApiSecret),
        string.IsNullOrEmpty(e.Passphrase) ? null : ApiKeyMasker.Mask(e.Passphrase),
        e.UserId,
        e.CreatedAt,
        e.UpdatedAt);
}
