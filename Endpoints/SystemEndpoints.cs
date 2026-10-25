using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Services;

namespace LuckyCat.Endpoints;

/// <summary>
/// 系统端点与统一错误包裹（LC-STD-03 §1.2/§3.6/§6）。
/// 生产 Program 与测试宿主共用这两个入口，保证测试证明的就是运行时行为。
/// </summary>
public static class SystemEndpoints
{
    /// <summary>服务启动时刻（进程内静态）；uptimeSeconds 汇报的是本服务存活时长，不是机器开机时长。</summary>
    private static readonly DateTime ServiceStartedAtUtc = DateTime.UtcNow;

    /// <summary>GET /api/health：统一包裹，只含公开运行状态字段，无任何密钥/连接串（§3.6）。</summary>
    public static void MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (AppDbContext db, CancellationToken ct) =>
        {
            var databaseOk = await db.Database.CanConnectAsync(ct);
            return Results.Ok(ApiResponse<object>.Ok(new
            {
                status = databaseOk ? "ok" : "degraded",
                version = Telemetry.ServiceVersion,
                database = databaseOk,
                uptimeSeconds = Math.Max(0, (long)(DateTime.UtcNow - ServiceStartedAtUtc).TotalSeconds),
                timestamp = DateTime.UtcNow,
            }));
        });
    }

    /// <summary>
    /// 未预期异常的兜底包裹（§6 internal_error 固定文案）：响应只给稳定错误码与可展示文案，
    /// 异常原文经 LogRedaction 脱敏后只进服务端日志，绝不进响应（LC-STD-02 §10）。
    /// 请求体绑定失败（含未映射字段、非法枚举/类型，BadHttpRequestException 400）在此统一映射为
    /// validation_failed，保证「请求体不得包含多余字段 → 4xx」在端点外也有确定形状。
    /// </summary>
    public static WebApplication UseApiErrorWrapper(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next();

                // 请求体绑定失败（未映射字段/非法枚举/坏 JSON）在 .NET 10 minimal API 里由框架直接写 400 空体、
                // 不抛异常；补齐统一包裹，保证「请求体不得包含多余字段 → 4xx 且有确定错误形状」（LC-STD-03 §1.1）。
                if (context.Response.StatusCode == StatusCodes.Status400BadRequest
                    && !context.Response.HasStarted
                    && string.IsNullOrEmpty(context.Response.ContentType))
                {
                    await WriteJsonAsync(context, ApiResponse<object>.Fail(
                        "validation_failed", "请求体校验失败：字段缺失、类型不合法或包含未定义字段"), 400);
                }
            }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("LuckyCat.Api");
                if (ex is BadHttpRequestException { StatusCode: 400 })
                {
                    logger?.LogWarning("请求体校验失败: {Detail}", LogRedaction.Scrub(ex.Message));
                    await WriteJsonAsync(context, ApiResponse<object>.Fail(
                        "validation_failed", "请求体校验失败：字段缺失、类型不合法或包含未定义字段"), 400);
                    return;
                }

                logger?.LogError(ex, "未预期异常: {Detail}", LogRedaction.Scrub(ex.ToString()));
                if (context.Response.HasStarted) throw;
                await WriteJsonAsync(context, ApiResponse<object>.Fail(
                    "internal_error", "服务内部错误，请稍后重试"), 500);
            }
        });
        return app;
    }

    private static Task WriteJsonAsync(HttpContext context, ApiResponse<object> body, int statusCode)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(body, ApiJson.Options);
    }
}
