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

    public static RouteGroupBuilder MapStrategies(this IEndpointRouteBuilder app)
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

        // —— 以下为旧前端端点（P7 前保留原形状）：/preview-prompt 与 /test-run（旧 JSON 演练，
        // 走 StrategyTestRunner 单模型调用，不创建 TradingCycle、无 SSE、无能力校验）。
        // Task 14 B 组将以 PromptLibrary 版 test-run（SSE + 周期落库 + provider 能力闸）替换 /test-run。——
        group.MapPost("/preview-prompt", async (
            PromptPreviewRequest request,
            CoinRankingService ranking,
            CancellationToken ct) =>
        {
            var error = ValidateConfig(request.Config);
            if (error is not null) return Results.BadRequest(new { message = error });

            // 币种来源（评分榜 / OI 榜 / 混合）真实解析后进入提示词；拉取失败回退为通用描述
            var coinSources = await ranking.ResolveAsync(request.Config!, ct);
            return Results.Ok(PromptComposer.Compose(request.Config!, coinSources is null
                ? null
                : new CoinSourceTexts(coinSources.ScoreDisplay, coinSources.OiTopDisplay, coinSources.OiLowDisplay, coinSources.MergedDisplay)));
        });

        group.MapPost("/test-run", async (
            StrategyTestRequest request,
            AppDbContext db,
            StrategyTestRunner runner,
            DecisionMemoryService memory,
            CancellationToken ct) =>
        {
            var error = ValidateConfig(request.Config);
            if (error is not null) return Results.BadRequest(new { message = error });

            var provider = await db.Providers.FindAsync(new object[] { request.ProviderId }, ct);
            if (provider is null) return Results.NotFound();

            // 未知策略不读不记（避免产生孤立记录）
            int? strategyId = request.StrategyId;
            if (strategyId is int sid && !await db.Strategies.AnyAsync(s => s.Id == sid, ct))
            {
                strategyId = null;
            }

            // 历史决策记忆：加载该策略最近记录（是否注入由策略开关决定，见 StrategyTestRunner）
            var history = strategyId is int loadId ? await memory.LoadRecentAsync(loadId, ct, fingerprint: PromptComposer.MemoryFingerprint(request.Config!)) : [];

            var result = await runner.RunAsync(provider, request.Config!, history, ct);

            // 记录落库：每轮都保存提示词、原始响应与失败阶段（「历史决策记忆」开关仅控制注入，关闭时仍持续积累）
            if (strategyId is int recordId)
            {
                await memory.RecordRoundAsync(recordId, null, provider, result, PromptComposer.MemoryFingerprint(request.Config!), ct);
            }

            return Results.Ok(result);
        });

        return group;
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
