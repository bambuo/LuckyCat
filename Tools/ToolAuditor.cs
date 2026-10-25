using System.Text;
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
/// 未预期异常统一兜底为 <c>tool_internal_error</c>（不回显异常原文，避免泄漏上游 URL/SQL）；
/// 取消（token 已取消）如实上抛。
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
            try
            {
                result = await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                result = ToolEnvelope.Failure(ToolErrors.ToolInternalError, "工具执行失败（内部错误）");
            }
        }
        var argsJson = JsonSerializer.Serialize(args, ApiJson.Options);
        var resultJson = JsonSerializer.Serialize(result, ApiJson.Options);
        var actualOverflow = Encoding.UTF8.GetByteCount(resultJson) > ToolEnvelope.MaxSerializedBytes;
        if (actualOverflow) resultJson = JsonSerializer.Serialize(new { truncated = true }, ApiJson.Options);
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
            Truncated = result.Truncated || actualOverflow,
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
