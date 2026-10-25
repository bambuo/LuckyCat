using System.Text;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;

namespace LuckyCat.Tools;

/// <summary>
/// 单周期工具与阶段预算：按角色计数（周期宿主实现；超出即拒绝继续调用），并提供阶段超时口径。
/// 默认不启用阶段超时（返回 0），保持纯计数实现的既有语义；生产实现按策略配置返回 90/90/45/45 秒。
/// </summary>
public interface ICycleBudget
{
    bool TryConsumeTool(string role);

    /// <summary>指定阶段（analyst/trader/risk/executor）的超时秒数；&lt;=0 表示不启用阶段超时。</summary>
    int StageTimeoutSeconds(string stage) => 0;

    /// <summary>
    /// 输入 token 预算闸门（LC-STD-01 §6、LC-STD-05 FLOW-09）：本周期累计输入用量达到预算即为超限，
    /// 后续阶段不再发起模型调用。默认不超限，保持纯计数实现的既有语义。
    /// </summary>
    bool IsInputTokenBudgetExceeded(Provider provider) => false;
}

/// <summary>
/// 所有工具共用的审计包装器：读工具也落 ToolInvocation（入参/回执/耗时/错误码/截断标记），
/// 消耗单周期工具预算，并在写工具或失败时向调用方传入的 SSE 频道发布 tool 事件——
/// 频道由 <see cref="LuckyCat.Workflows.TradingCycleContext.SseChannel"/> 单点决策（试运行走
/// test-run 周期频道，其余走交易员频道；R-11-7），为空表示该装配位不推 SSE。
/// 未预期异常统一兜底为 <c>tool_internal_error</c>（不回显异常原文，避免泄漏上游 URL/SQL）；
/// 取消（token 已取消）如实上抛。
/// </summary>
public sealed class ToolAuditor(AppDbContext db, SseEventHub hub, ICycleBudget budget,
    string? cycleId, string stageRunId, string? sseChannel)
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
        if (sseChannel is { } channel && (ToolCatalog.IsWrite(tool) || !result.Ok))
        {
            hub.Publish(channel, new SseEvent("tool", new
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
