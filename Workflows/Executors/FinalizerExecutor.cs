using Microsoft.Agents.AI.Workflows;

namespace LuckyCat.Workflows.Executors;

/// <summary>
/// 收尾阶段：不调用模型、不产生 AgentStageRun（LC-STD-01 §4.1）。输入为任一终态消息
/// （ResearchBriefMessage/ProposalMessage/VerdictMessage/ExecutionReport），仅用于让条件边把控制权交回；
/// 终态一律从 <see cref="TradingCycleContext.Outcome"/> 读取（阶段执行器写入），落库与 SSE 推送
/// 经 <see cref="ICycleRecorder.CompleteCycleAsync"/> 交给周期宿主实现。
/// 终态缺失说明路由或阶段写入有误，按 failed 记录，绝不静默 completed。
/// </summary>
public sealed class FinalizerExecutor(TradingCycleContext ctx)
    : Executor<object, CycleSignal>("finalizer"), IStageExecutor
{
    public string Stage => "finalizer";

    public override async ValueTask<CycleSignal> HandleAsync(
        object input, IWorkflowContext context, CancellationToken ct)
    {
        var outcome = ctx.Outcome ?? new CycleOutcome("failed", "missing_outcome", null);
        if (ctx.Outcome is null)
        {
            ctx.SetOutcome(outcome);
        }

        await ctx.Recorder.CompleteCycleAsync(ctx, outcome.Status, outcome.ErrorCode, outcome.Error, ct);
        return new CycleSignal(outcome.Status);
    }
}
