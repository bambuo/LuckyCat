using System.Diagnostics;
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

        // 输入 token 预算闸（LC-STD-01 §6、FLOW-09）：超限不再发起模型调用；
        // fail-closed——执行停止、绝不发起写请求，终态记「观望完成」。
        if (ctx.TokenBudgetExhausted())
        {
            ctx.SetOutcome(new CycleOutcome("completed", "token_budget_exceeded",
                "输入 token 预算已达上限，执行阶段停止（未下单），本周期观望"));
            return new ExecutionReport("输入 token 预算已达上限，本周期观望，未发起任何写请求", [], [], []);
        }

        var agent = factory.Create(client, AgentRole.Executor, ctx.Config,
            tools.For(AgentRole.Executor, StageRunId), null);
        var prompt = await PromptLibrary.BuildExecutorUserPrompt(ctx, proposal, verdict, ct);
        // 阶段超时（LC-STD-01 §6）：执行超时同样按失败终止，绝不留下「已提交但阶段未完成」的中间态。
        using var stageTimeout = ctx.StageTimeout(ct, Stage);
        var started = Stopwatch.StartNew();
        AgentRunResult<ExecutionReport> result;
        try
        {
            result = await runner.RunAsync<ExecutionReport>(agent, prompt, stageTimeout.Token);
        }
        catch (OperationCanceledException) when (stageTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 仅阶段自身超时写入计时；用户停止/周期超时的取消不得伪装成阶段超时。
            ctx.MarkStageTimeout(Stage, started.ElapsedMilliseconds);
            throw;
        }

        var report = result.Output;
        var stats = StageRunStats.From(result.Messages, result.Usage, started.ElapsedMilliseconds);

        await ctx.Recorder.RecordStageAsync(Stage, StageRunId, report,
            PromptLibrary.Build(AgentRole.Executor, ctx.Config), stats, 1, ct);

        ctx.SetOutcome(report.Errors.Count > 0
            ? new CycleOutcome("completed", "execution_errors", string.Join("; ", report.Errors))
            : new CycleOutcome("completed", null, null));

        return report;
    }
}
