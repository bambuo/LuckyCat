using System.Globalization;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;

namespace LuckyCat.Endpoints;

public static class StrategiesEndpoints
{
    private static readonly string[] ValidStrategyTypes = ["ai_trading", "grid_trading"];

    private static readonly AgentRole[] PreviewRoles =
    [
        AgentRole.Analyst, AgentRole.Trader, AgentRole.Risk, AgentRole.Executor, AgentRole.Reviewer,
    ];

    extension(IEndpointRouteBuilder app)
    {
        public RouteGroupBuilder MapStrategies()
        {
            var group = app.MapGroup("/api/strategies");

            group.MapGet("/", async (AppDbContext db) =>
            {
                var items = await db.Strategies.OrderByDescending(s => s.Id).ToListAsync();
                return ApiResults.Ok(items.Select(ToDto).ToList());
            });

            group.MapPost("/", async (StrategyUpsertRequest request, AppDbContext db) =>
            {
                var error = Validate(request);
                if (error is not null) return ApiResults.Fail("validation_failed", error, 400);

                var now = DateTime.UtcNow;
                var strategy = new Strategy
                {
                    Name = request.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                    ConfigJson = SerializeConfig(request.Config!),
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Strategies.Add(strategy);
                await db.SaveChangesAsync();
                return ApiResults.Created(ToDto(strategy));
            });

            group.MapPut("/{id:int}", async (int id, StrategyUpsertRequest request, AppDbContext db) =>
            {
                var strategy = await db.Strategies.FindAsync(new object[] { id });
                if (strategy is null) return ApiResults.Fail("not_found", "策略不存在", 404);

                var error = Validate(request);
                if (error is not null) return ApiResults.Fail("validation_failed", error, 400);

                strategy.Name = request.Name.Trim();
                strategy.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
                strategy.ConfigJson = SerializeConfig(request.Config!);
                strategy.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync();
                return ApiResults.Ok(ToDto(strategy));
            });

            group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
            {
                var strategy = await db.Strategies.FindAsync(new object[] { id });
                if (strategy is null) return ApiResults.Fail("not_found", "策略不存在", 404);

                // 被交易员或历史周期引用的策略不得硬删除（LC-STD-03 §1.4/§3.3：审计链完整）
                if (await db.Traders.AnyAsync(t => t.StrategyId == id)
                    || await db.TradingCycles.AnyAsync(c => c.StrategyId == id))
                {
                    return ApiResults.Fail("resource_in_use", "该策略仍被交易员或历史周期引用，无法删除", 409);
                }

                db.Strategies.Remove(strategy);
                await db.SaveChangesAsync();
                return Results.NoContent();
            });

            group.MapPost("/{id:int}/activate", async (int id, AppDbContext db) =>
            {
                var strategy = await db.Strategies.FindAsync(new object[] { id });
                if (strategy is null) return ApiResults.Fail("not_found", "策略不存在", 404);

                var actives = await db.Strategies.Where(s => s.IsActive && s.Id != id).ToListAsync();
                foreach (var other in actives) other.IsActive = false;
                strategy.IsActive = true;
                await db.SaveChangesAsync();
                return ApiResults.Ok(ToDto(strategy));
            });

            group.MapPost("/{id:int}/duplicate", async (int id, AppDbContext db) =>
            {
                var source = await db.Strategies.FindAsync(new object[] { id });
                if (source is null) return ApiResults.Fail("not_found", "策略不存在", 404);

                var now = DateTime.UtcNow;
                var copy = new Strategy
                {
                    Name = Truncate($"{source.Name} 副本", 100),
                    Description = source.Description,
                    IsActive = false,
                    ConfigJson = source.ConfigJson,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Strategies.Add(copy);
                await db.SaveChangesAsync();
                return ApiResults.Created(ToDto(copy));
            });

            // 字面量路由优先于 /{id:int}，无冲突
            group.MapGet("/default-config", () =>
                ApiResults.Ok(new StrategyDefaultsDto(PromptComposer.CreateDefaultConfig(), PromptComposer.CreateDefaultSections())));

            // 五角色系统提示词预览（LC-STD-03 §3.3）：与正式周期同一 PromptLibrary.Build 产物，
            // 不调用模型、不落库、不改记忆指纹（LC-STD-04 §7：预览不是模板变更，不 bump 修订号）。
            group.MapPost("/preview-prompts", (PromptPreviewRequest request) =>
            {
                var error = ValidateConfig(request.Config);
                if (error is not null) return ApiResults.Fail("validation_failed", error, 400);

                var roles = PreviewRoles.Select(role =>
                {
                    var prompt = PromptLibrary.Build(role, request.Config!);
                    return new RolePromptPreviewDto(role, prompt, StrategyTestRunner.EstimateTokens(prompt));
                }).ToList();
                return ApiResults.Ok(new PromptPreviewResultDto(roles));
            });

            // 配置校验（LC-STD-03 §3.3/§6）：通过→200 data{valid,errors}；不通过→400 validation_failed 点名字段。
            group.MapPost("/validate", (PromptPreviewRequest request) =>
            {
                var error = ValidateConfig(request.Config);
                return error is not null
                    ? ApiResults.Fail("validation_failed", error, 400)
                    : ApiResults.Ok(new StrategyValidationDto(true, []));
            });

            // 试运行（LC-STD-03 §3.3，R-14-8 替换旧单发演练）：真实图、SSE 事件集、trigger=test、
            // mode=dry、合成账户（SyntheticAccountReader：委托风控策略/纪律，不伪造数值），
            // 周期 traderId/exchangeAccountId 落 null。能力闸 422（API-09）；order 事件因写工具
            // 周期归属硬闸 fail-closed 永不出现（API-06），tool 帧带 errorCode + 中文摘要供 UI 呈现拒绝。
            // 旧 /preview-prompt（单数）与旧 JSON 演练 /test-run 已删除——前端同步迁移（Task 15-17，
            // 计划内中间断裂，见 task-14 报告）。
            group.MapPost("/test-run", async (
                StrategyRunTestRequest request,
                AppDbContext db,
                TradingCycleRunner runner,
                HttpContext http,
                CancellationToken ct) =>
            {
                var strategy = await db.Strategies.FindAsync(new object[] { request.StrategyId }, ct);
                if (strategy is null) return ApiResults.Fail("not_found", "策略不存在", 404);
                var provider = await db.Providers.FindAsync(new object[] { request.ProviderId }, ct);
                if (provider is null) return ApiResults.Fail("not_found", "提供商不存在", 404);

                // API-09：未通过工具调用验证（或被编辑重置）的提供商不得试运行。
                if (provider.ToolCallingSupported != true || provider.ToolCallingVerifiedAt is null)
                {
                    return ApiResults.Fail("provider_capability_missing",
                        "该提供商未通过工具调用验证，请先完成验证再试运行", 422);
                }

                // accountId 为契约预留：B 组实现只支持合成账户演练（R-14-8，见 task-14 报告）。
                if (request.AccountId is not null)
                {
                    return ApiResults.Fail("validation_failed",
                        "试运行仅支持合成账户（accountId 必须为空）", 400);
                }

                var equityText = string.IsNullOrWhiteSpace(request.TestEquity) ? "10000" : request.TestEquity;
                if (!decimal.TryParse(equityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var testEquity)
                    || testEquity <= 0)
                {
                    return ApiResults.Fail("validation_failed", "testEquity 必须是大于 0 的数字字符串", 400);
                }

                http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
                http.Response.Headers.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["X-Accel-Buffering"] = "no";

                // state 种子帧（§4.2 触发列「连接建立」；试运行的 isRunning 指本次演练）。
                var writeLock = new SemaphoreSlim(1, 1);
                await WriteTestFrameAsync(http, writeLock, "state",
                    new TraderStateEvent(true, null, null), ct);

                // 心跳：静默期每 15 秒一帧（§4.1），与事件写共用串行闸。
                using var pingTimer = new System.Timers.Timer(15_000);
                pingTimer.AutoReset = true;
                pingTimer.Elapsed += async (_, _) =>
                {
                    try
                    {
                        await WriteTestFrameAsync(http, writeLock, "ping", new { at = DateTime.UtcNow },
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Api] test-run 心跳写入失败（连接可能已断开）：{ex.GetType().Name}");
                    }
                };
                pingTimer.Start();

                await runner.RunTestAsync(provider, strategy, testEquity, request.IncludeMemory,
                    async evt =>
                    {
                        await writeLock.WaitAsync(CancellationToken.None);
                        try
                        {
                            await http.Response.WriteAsync($"event: {evt.Name}\ndata: " +
                                $"{JsonSerializer.Serialize(evt.Payload, FrameJson)}\n\n", ct);
                            await http.Response.Body.FlushAsync(ct);
                        }
                        finally
                        {
                            writeLock.Release();
                        }
                    }, ct);
                pingTimer.Stop();

                return Results.Empty;
            });

            return group;
        }
    }

    private static readonly JsonSerializerOptions FrameJson = new(JsonSerializerDefaults.Web);

    private static async Task WriteTestFrameAsync(HttpContext http, SemaphoreSlim writeLock,
        string name, object payload, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await http.Response.WriteAsync($"event: {name}\ndata: " +
                $"{JsonSerializer.Serialize(payload, FrameJson)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static string? Validate(StrategyUpsertRequest request) =>
        string.IsNullOrWhiteSpace(request.Name) ? "名称不能为空" : ValidateConfig(request.Config);

    // 供 preview-prompt / preview-prompts / validate / test-run 复用
    private static string? ValidateConfig(StrategyConfig? config)
    {
        if (config is null) return "config 不能为空";
        if (config.RiskControl is null || config.Indicators?.Klines is null || config.CoinSource is null || config.PromptSections is null)
            return "风控、指标、币种来源和提示词配置不能为空";
        if (string.IsNullOrWhiteSpace(config.StrategyType))
        {
            config.StrategyType = "ai_trading";   // 缺省按智能交易
        }
        return ValidStrategyTypes.Contains(config.StrategyType)
            ? TradingRiskPolicy.ValidateConfig(config.RiskControl)
            : "config.strategyType 无效（仅支持 ai_trading / grid_trading）";
    }

    private static string SerializeConfig(StrategyConfig config) =>
        JsonSerializer.Serialize(config, VerificationJson.Options);

    private static StrategyConfig ParseConfig(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StrategyConfig>(json, VerificationJson.Options) ?? new StrategyConfig();
        }
        catch (JsonException)
        {
            // 理论上不会发生（写入时已校验可序列化）；兜底返回默认配置而非 500
            return new StrategyConfig();
        }
    }

    private static StrategyDto ToDto(Strategy s) => new(
        s.Id, s.Name, s.Description, s.IsActive, ParseConfig(s.ConfigJson), s.CreatedAt, s.UpdatedAt);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
