using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;

namespace LuckyCat.Tools;

/// <summary>单周期工具预算：按角色计数（周期宿主实现；超出即拒绝继续调用）。</summary>
public interface ICycleBudget
{
    bool TryConsumeTool(string role);
}

/// <summary>
/// 所有工具共用的审计包装器：读工具也落 ToolInvocation（入参/回执/耗时/错误码/截断标记），
/// 消耗单周期工具预算，并在写工具或失败时向该交易员的 SSE 频道发布 tool 事件。
/// </summary>
public sealed class ToolAuditor(AppDbContext db, SseEventHub hub, ICycleBudget budget,
    string? cycleId, string stageRunId, int? traderId)
{
    public async Task<ToolEnvelope> RunAsync(string tool, string role, object args,
        Func<Task<ToolEnvelope>> action, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        ToolEnvelope result;
        if (!budget.TryConsumeTool(role))
        {
            result = ToolEnvelope.Failure(ToolErrors.ToolBudgetExceeded, "工具调用超预算");
        }
        else
        {
            try { result = await action(); }
            catch (Exception ex) { result = ToolEnvelope.Failure(ToolErrors.ToolInternalError, ex.Message); }
        }
        var argsJson = JsonSerializer.Serialize(args, ApiJson.Options);
        var resultJson = JsonSerializer.Serialize(result, ApiJson.Options);
        var truncated = resultJson.Length > 16 * 1024;
        if (truncated) resultJson = resultJson[..(16 * 1024)];
        db.ToolInvocations.Add(new ToolInvocation
        {
            CycleId = cycleId,
            StageRunId = stageRunId,
            ToolName = tool,
            ArgumentsJson = argsJson,
            ResultJson = resultJson,
            Success = result.Ok,
            ErrorCode = result.Error?.Code,
            ElapsedMs = started.ElapsedMilliseconds,
            Truncated = truncated,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        if (traderId is { } tid && (ToolCatalog.IsWrite(tool) || !result.Ok))
        {
            hub.Publish(SseChannels.Trader(tid), new SseEvent("tool", new
            {
                cycleId,
                stageId = stageRunId,
                tool,
                success = result.Ok,
                errorCode = result.Error?.Code,
                elapsedMs = started.ElapsedMilliseconds,
                at = DateTime.UtcNow
            }));
        }
        return result;
    }
}
