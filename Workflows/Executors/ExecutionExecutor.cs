using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Workflows;

namespace LuckyCat.Workflows.Executors;

/// <summary>
/// 执行员阶段：输入为 <see cref="VerdictMessage"/>（open 获批）或 <see cref="ProposalMessage"/>（close，
/// 无裁决，FLOW-14）。其余消息类型一律 fail-closed 抛出，绝不按未知输入下单。
/// 报告 errors 非空时写入 completed + execution_errors 终态（FLOW-05：周期完成但 errors 非空）。
/// 写工具的硬闸、预算与超时属于工具与宿主层（LC-STD-01 §6）。
/// </summary>
public sealed class ExecutionExecutor(TradingCycleContext ctx, TradingAgentFactory factory,
    AgentRunner runner, ToolRegistry tools, IChatClient client)
    : Executor<object, ExecutionReport>("executor"), IStageExecutor
{
    public string Stage => "executor";

    /// <summary>本执行器实例绑定的阶段运行 ID：ToolAuditor 落 ToolInvocation 用同一个 ID，
    /// 周期宿主（Task 11）可据此把工具审计行与 AgentStageRun 对齐。</summary>
    public string StageRunId { get; } = Ids.NewUlid();

    public override async ValueTask<ExecutionReport> HandleAsync(
        object input, IWorkflowContext context, CancellationToken ct)
    {
        var (proposal, verdict) = input switch
        {
            VerdictMessage verdictMessage => (verdictMessage.Proposal, verdictMessage.Verdict),
            ProposalMessage proposalMessage => (proposalMessage.Proposal, null),
            _ => throw new InvalidOperationException(
                $"执行阶段收到非法输入 {input.GetType().Name}，fail-closed 终止"),
        };

        var agent = factory.Create(client, AgentRole.Executor, ctx.Config,
            tools.For(AgentRole.Executor, StageRunId), null);
        var prompt = await PromptLibrary.BuildExecutorUserPrompt(ctx, proposal, verdict, ct);
        var (report, _) = await runner.RunAsync<ExecutionReport>(agent, prompt, ct);

        await ctx.Recorder.RecordStageAsync(Stage, report,
            PromptLibrary.Build(AgentRole.Executor, ctx.Config), ct);

        ctx.SetOutcome(report.Errors.Count > 0
            ? new CycleOutcome("completed", "execution_errors", string.Join("; ", report.Errors))
            : new CycleOutcome("completed", null, null));

        return report;
    }
}
