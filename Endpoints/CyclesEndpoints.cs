using System.Globalization;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Workflows;
using Microsoft.EntityFrameworkCore;

namespace LuckyCat.Endpoints;

/// <summary>
/// 周期端点（LC-STD-03 §3.5/§5.1、LC-STD-05 API-08）：周期详情与阶段全文。
/// 只读无副作用；试运行周期（<c>traderId=null</c>）同样可取回，且不泄漏任何交易员/账户关联
/// （详情只由 cycleId 反查自身行，R-14-9）。金额/数量/百分比一律字符串、时间一律 UTC（§1.1）；
/// <c>strategyConfigJson</c> 与 <c>memoryFingerprint</c> 仅存库审计、不出现在响应（§2.1）。
/// </summary>
public static class CyclesEndpoints
{
    public static RouteGroupBuilder MapCycles(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cycles");

        // 周期详情：阶段、提案、裁决（仅 open 路径）、订单（API-08）。
        group.MapGet("/{cycleId}", async (string cycleId, AppDbContext db, CancellationToken ct) =>
        {
            var cycle = await db.TradingCycles.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == cycleId, ct);
            if (cycle is null) return ApiResults.Fail("not_found", "交易周期不存在", 404);

            return ApiResults.Ok(await CycleDtoMapper.BuildDetailAsync(db, cycle, ct));
        });

        // 阶段全文：消息记录原文（受截断标记，§3.5）+ 工具调用摘要（不含入参/回执全文，§4.2 同口径）。
        group.MapGet("/{cycleId}/stages/{stageRunId}", async (
            string cycleId, string stageRunId, AppDbContext db, CancellationToken ct) =>
        {
            var cycleExists = await db.TradingCycles.AsNoTracking().AnyAsync(c => c.Id == cycleId, ct);
            if (!cycleExists) return ApiResults.Fail("not_found", "交易周期不存在", 404);

            // 阶段必须属于该周期：跨周期取回按 not_found 处理，不泄漏他人阶段。
            var stage = await db.AgentStageRuns.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == stageRunId && s.CycleId == cycleId, ct);
            if (stage is null) return ApiResults.Fail("not_found", "阶段不存在或不属于该周期", 404);

            var toolCalls = await db.ToolInvocations.AsNoTracking()
                .Where(t => t.StageRunId == stageRunId)
                .OrderBy(t => t.Id)
                .Select(t => new StageToolCallDto(t.ToolName, t.Success, t.ErrorCode, t.ElapsedMs,
                    t.Truncated, CycleDtoMapper.Utc(t.CreatedAt)))
                .ToListAsync(ct);

            return ApiResults.Ok(new StageDetailDto(
                stage.Id, stage.CycleId, stage.TraderId, stage.Role, stage.Attempt, stage.Status,
                stage.ProviderName, stage.Model, stage.Instructions, stage.TranscriptJson, stage.OutputJson,
                stage.Reasoning, stage.InputTokens, stage.OutputTokens, stage.ElapsedMs, stage.ToolCallCount,
                stage.Error, stage.Truncated, CycleDtoMapper.Utc(stage.CreatedAt), toolCalls));
        });

        return group;
    }
}

/// <summary>
/// 周期实体 → 契约 DTO 的唯一映射位（周期详情、看板最新周期块、手动平仓回执共用）：
/// 数值一律 <see cref="Num"/> 字符串化（LC-STD-03 §1.1），时刻一律显式标 UTC（SQLite 回读丢失 Kind）。
/// </summary>
internal static class CycleDtoMapper
{
    public static async Task<CycleDetailDto> BuildDetailAsync(AppDbContext db, TradingCycle cycle,
        CancellationToken ct)
    {
        var stages = await db.AgentStageRuns.AsNoTracking()
            .Where(s => s.CycleId == cycle.Id)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);
        // 一提案一路径；hold/close 没有裁决（API-08：裁决仅随 open 路径返回，即使库里有游离行也不外泄）。
        var proposal = await db.TradeProposals.AsNoTracking()
            .Where(p => p.CycleId == cycle.Id)
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
        RiskVerdict? verdict = null;
        if (proposal is { Action: "open" })
        {
            verdict = await db.RiskVerdicts.AsNoTracking()
                .FirstOrDefaultAsync(v => v.ProposalId == proposal.Id, ct);
        }

        var orders = await db.TraderOrders.AsNoTracking()
            .Where(o => o.CycleId == cycle.Id)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        return new CycleDetailDto(
            ToInfo(cycle),
            stages.Select(ToStage).ToList(),
            ToProposal(proposal),
            ToVerdict(verdict),
            orders.Select(ToOrder).ToList());
    }

    /// <summary>周期最新一行的详情块（看板数据源，§3.4）；无周期时为 null（未知，不是空对象）。</summary>
    public static async Task<CycleDetailDto?> BuildLatestAsync(AppDbContext db, int traderId, CancellationToken ct)
    {
        var cycle = await db.TradingCycles.AsNoTracking()
            .Where(c => c.TraderId == traderId)
            .OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        return cycle is null ? null : await BuildDetailAsync(db, cycle, ct);
    }

    public static CycleInfoDto ToInfo(TradingCycle c) => new(
        c.Id, c.TraderId, c.StrategyId, c.ProviderId, c.ExchangeAccountId, c.Mode, c.Trigger, c.Status,
        Utc(c.StartedAt), Utc(c.CompletedAt), c.ErrorCode, c.Error,
        c.InputTokens, c.OutputTokens, c.ElapsedMs);

    public static CycleStageDto ToStage(AgentStageRun s) => new(
        s.Id, s.Role, s.Status, s.ElapsedMs, s.InputTokens, s.OutputTokens, s.ToolCallCount,
        StageSummary(s), s.Error);

    public static TradeProposalDto? ToProposal(TradeProposal? p) => p is null
        ? null
        : new TradeProposalDto
        {
            Id = p.Id,
            Action = p.Action,
            Symbol = p.Symbol,
            Side = p.Side,
            Confidence = Num(p.Confidence),
            EntryPrice = Num(p.EntryPrice),
            StopLossPrice = Num(p.StopLossPrice),
            TakeProfitPrice = Num(p.TakeProfitPrice),
            PositionPct = Num(p.PositionPct),
            Leverage = Num(p.Leverage),
            Thesis = p.Thesis,
            Invalidation = p.Invalidation,
        };

    public static CycleVerdictDto? ToVerdict(RiskVerdict? v) => v is null
        ? null
        : new CycleVerdictDto(
            v.Id, v.Decision, ReadStringArray(v.ReasonsJson),
            Num(v.MaxPositionPct), Num(v.MaxLeverage), Num(v.EntryZoneMin), Num(v.EntryZoneMax),
            v.AllowMarket, Utc(v.ValidUntilUtc));

    public static CycleOrderDto ToOrder(TraderOrder o) => new(
        o.Id, o.CycleId, o.TraderId, o.ExchangeAccountId, o.ProposalId, o.Action, o.Side, o.Symbol,
        Num(o.Quantity), Num(o.FilledQuantity), Num(o.Price), Num(o.AveragePrice),
        o.Status, o.Simulated, o.Note, Utc(o.At));

    public static string? Num(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>SQLite 回读的 DateTime 丢失 Kind：出参前一律显式标 UTC（§1.1「时间统一 UTC」）。</summary>
    public static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? Utc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    /// <summary>
    /// 阶段摘要从落库的 <c>outputJson</c> 快照推导，与 <see cref="CycleRecorder"/> 的 stage 事件摘要同构；
    /// 不新增落库列、不重跑模型。JSON 异常时返回 null（未知），不影响其余字段。
    /// </summary>
    private static string? StageSummary(AgentStageRun s)
    {
        if (string.IsNullOrEmpty(s.OutputJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(s.OutputJson);
            var root = doc.RootElement;
            return s.Role switch
            {
                "analyst" when root.TryGetProperty("marketContext", out var ctx)
                    => $"候选 {CountArray(root, "candidates")} 个；{ctx.GetString()}",
                "trader" when root.TryGetProperty("proposal", out var proposal)
                    => $"{GetPropertyOrEmpty(proposal, "action")} {GetPropertyOrEmpty(proposal, "symbol")}：" +
                       $"{GetPropertyOrEmpty(proposal, "thesis")}",
                "risk" when root.TryGetProperty("verdict", out var verdict)
                    => $"{GetPropertyOrEmpty(verdict, "decision")}：" + string.Join("；", ReadStringArray(verdict.TryGetProperty("reasons", out var r) ? r.ToString() : null)),
                "executor" when root.TryGetProperty("summary", out var summary) => summary.GetString(),
                "reviewer" when root.TryGetProperty("summary", out var reviewSummary) => reviewSummary.GetString(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int CountArray(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static string GetPropertyOrEmpty(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>JSON 字符串数组列的读取：null/非法一律回空数组（有数据集但为空，§1.1 三语义）。</summary>
    public static IReadOnlyList<string> ReadStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>周期状态白名单（§2.1/§4.2；API-10 的判定口径）。</summary>
    public static readonly string[] CycleStatuses = ["running", "completed", "failed", "skipped", "aborted"];
}
