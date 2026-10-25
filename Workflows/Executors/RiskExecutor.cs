using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Workflows;

namespace LuckyCat.Workflows.Executors;

/// <summary>
/// 风控官阶段：仅 open 提案进入（close 是降险动作，跳过风控，FLOW-14）。
/// 裁决与提案一起作为 <see cref="VerdictMessage"/> 下发；rejected 写入 completed 终态并走收尾边（FLOW-03）。
/// 只收紧不放宽的边界校验由工具硬闸兜底（LC-STD-01 §5.3）。
/// </summary>
public sealed class RiskExecutor(TradingCycleContext ctx, TradingAgentFactory factory,
    AgentRunner runner, ToolRegistry tools, IChatClient client)
    : Executor<ProposalMessage, VerdictMessage>("risk"), IStageExecutor
{
    public string Stage => "risk";

    /// <summary>本执行器实例绑定的阶段运行 ID：ToolAuditor 落 ToolInvocation 用同一个 ID，
    /// 周期宿主（Task 11）可据此把工具审计行与 AgentStageRun 对齐。</summary>
    public string StageRunId { get; } = Ids.NewUlid();

    public override async ValueTask<VerdictMessage> HandleAsync(
        ProposalMessage input, IWorkflowContext context, CancellationToken ct)
    {
        var agent = factory.Create(client, AgentRole.Risk, ctx.Config,
            tools.For(AgentRole.Risk, StageRunId), null);
        var prompt = await ctx.BuildUserPrompt(AgentRole.Risk, ct);
        var (verdict, _) = await runner.RunAsync<AgentRiskVerdict>(agent, prompt, ct);

        // 契约校验（LC-STD-01 §4.1 第 3 步）：同 TraderExecutor，越界 RiskDecision 匹配不到
        // approved/rejected 任一出边会死路；Rejected=0 是有意的安全零值，fail-closed 归一化为 Rejected。
        if (!Enum.IsDefined(verdict.Decision))
        {
            ctx.SetOutcome(new CycleOutcome("failed", "stage_contract_violation",
                "风控裁决结果越界，本周期已按失败安全终止"));
            var safeVerdict = verdict with { Decision = RiskDecision.Rejected };
            var safeMessage = new VerdictMessage(input.Proposal, safeVerdict);
            ctx.Observe(safeVerdict);
            await ctx.Recorder.RecordStageAsync(Stage, safeMessage,
                PromptLibrary.Build(AgentRole.Risk, ctx.Config), ct);
            return safeMessage;
        }

        var message = new VerdictMessage(input.Proposal, verdict);
        ctx.Observe(verdict);
        await ctx.Recorder.RecordStageAsync(Stage, message,
            PromptLibrary.Build(AgentRole.Risk, ctx.Config), ct);

        if (verdict.Decision == RiskDecision.Rejected)
        {
            ctx.SetOutcome(new CycleOutcome("completed", null, null));
        }

        return message;
    }
}
