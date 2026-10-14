using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>
/// 历史决策记忆读写（手工 AI 测试端点与交易员后台执行共用）：
/// 读取某策略最近记录（注入用）与落库（包含调用/契约失败；开关只影响成功记忆的注入）。
/// </summary>
public class DecisionMemoryService(AppDbContext db, SseEventHub hub)
{
    public async Task<List<DecisionRecord>> LoadRecentAsync(int strategyId, CancellationToken ct, int? traderId = null, string? fingerprint = null)
    {
        var query = db.DecisionRecords.Where(r => r.StrategyId == strategyId && r.TraderId == traderId && r.Action != "error");
        if (fingerprint is not null)
        {
            var marker = $"\"memoryFingerprint\":\"{fingerprint}\"";
            query = query.Where(r => r.DecisionJson.Contains(marker));
        }
        var records = await query.OrderByDescending(r => r.Id).Take(PromptComposer.MemoryRecordLimit).ToListAsync(ct);
        if (traderId is null)
        {
            foreach (var r in records) r.ExecutionSummary = "手工测试，仅建议，未执行";
            return records;
        }
        if (records.Count == 0) return records;
        var since = records.Min(r => r.CreatedAt);
        var orders = await db.TraderOrders.Where(o => o.TraderId == traderId && o.At >= since).OrderBy(o => o.At).ToListAsync(ct);
        foreach (var r in records)
        {
            var next = records.Where(n => n.Id > r.Id).Select(n => (DateTime?)n.CreatedAt).Min() ?? DateTime.MaxValue;
            var execution = orders.Where(o => o.At >= r.CreatedAt && o.At < next).ToList();
            r.ExecutionSummary = r.Action == "hold" ? "观望，无执行动作" : execution.Count == 0 ? "执行结果未确认，不得视为已成交" :
                string.Join("；", execution.Select(o => $"{o.Action}: {o.Note ?? o.Error ?? (o.Success ? "请求成功，成交明细未核实" : "失败")}"));
        }
        return records;
    }

    public Task RecordRoundAsync(int strategyId, int? traderId, Provider provider,
        StrategyTestResultDto result, string fingerprint, CancellationToken ct)
    {
        var node = result.Decision is { } decision ? JsonNode.Parse(decision.GetRawText())!.AsObject() : new JsonObject();
        var error = result.Error ?? (result.Decision is null ? "未产生有效决策" : null);
        if (error is not null)
        {
            node = new JsonObject { ["action"] = "error", ["reason"] = error };
        }
        string Scrub(string text) => string.IsNullOrEmpty(provider.ApiKey) ? text : text.Replace(provider.ApiKey, "<redacted>", StringComparison.Ordinal);
        node["_round"] = JsonSerializer.SerializeToNode(new DecisionRoundMetadata(error is null ? "ok" : "error",
            result.FailureStage, error is null ? null : Scrub(error), Scrub(result.RawOutput), PromptComposer.Revision, fingerprint, result.RequestSent), PromptJson.Options);
        if (error is not null) node["reason"] = Scrub(error);
        var payload = JsonSerializer.SerializeToElement(node, PromptJson.Options);
        return RecordAsync(strategyId, traderId, provider, payload, result.Reasoning, result.InputTokens, result.OutputTokens,
            result.ElapsedMs, result.SystemPrompt, result.UserPrompt, ct);
    }

    /// <summary>决策落库（含思维链/token 用量/耗时，以及系统与用户提示词原文——完整落库；落库失败不影响调用方返回）。</summary>
    public async Task RecordAsync(
        int strategyId,
        int? traderId,
        Provider provider,
        JsonElement decision,
        string? reasoning,
        int? inputTokens,
        int? outputTokens,
        long? elapsedMs,
        string? systemPrompt,
        string? userPrompt,
        CancellationToken ct)
    {
        DecisionRecord? saved = null;
        var persisted = false;
        try
        {
            var json = decision.GetRawText();
            saved = new DecisionRecord
            {
                StrategyId = strategyId,
                TraderId = traderId,
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                Model = provider.Model,
                Symbol = ReadString(decision, "symbol"),
                Action = ReadString(decision, "action"),
                Confidence = ReadDouble(decision, "confidence"),
                DecisionJson = json,
                Reasoning = Truncate(reasoning, 2000),
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ElapsedMs = elapsedMs,
                CreatedAt = DateTime.UtcNow,
            };
            db.DecisionRecords.Add(saved);
            await db.SaveChangesAsync(ct);
            persisted = true;
        }
        catch (Exception ex)
        {
            // 落库失败不影响本轮结果返回
            Console.WriteLine($"[DecisionMemory] 决策记录落库失败：{ex.Message}");
        }

        // 落库成功即广播 SSE 事件（交易员实盘/演练周期才有 TraderId；看板连接的转发由 SSE 端点订阅完成）
        if (persisted && saved is { TraderId: int tid })
        {
            hub.Publish(SseChannels.Trader(tid), new SseEvent("decision", saved));
        }
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : (value.Length > max ? value[..max] : value);

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
}
