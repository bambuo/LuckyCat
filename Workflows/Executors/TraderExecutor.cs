using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Workflows;

namespace LuckyCat.Workflows.Executors;

/// <summary>
/// 交易员阶段：消费研究简报，结合账户/持仓/记忆读工具产出提案；
/// hold 写入 completed 终态并直接走收尾条件边（FLOW-02）。
/// </summary>
public sealed class TraderExecutor(TradingCycleContext ctx, TradingAgentFactory factory,
    AgentRunner runner, ToolRegistry tools, IChatClient client)
    : Executor<ResearchBriefMessage, ProposalMessage>("trader"), IStageExecutor
{
    public string Stage => "trader";

    /// <summary>本执行器实例绑定的阶段运行 ID：ToolAuditor 落 ToolInvocation 用同一个 ID，
    /// 周期宿主（Task 11）可据此把工具审计行与 AgentStageRun 对齐。</summary>
    public string StageRunId { get; } = Ids.NewUlid();

    public override async ValueTask<ProposalMessage> HandleAsync(
        ResearchBriefMessage input, IWorkflowContext context, CancellationToken ct)
    {
        var agent = factory.Create(client, AgentRole.Trader, ctx.Config,
            tools.For(AgentRole.Trader, StageRunId), null);
        var prompt = await PromptLibrary.BuildTraderUserPrompt(ctx, input.Brief, ct);
        var (proposal, _) = await runner.RunAsync<AgentTradeProposal>(agent, prompt, ct);

        // 契约校验（LC-STD-01 §4.1 第 3 步）：System.Text.Json 会原样接受越界枚举值，
        // 未定义动作匹配不到任何出边会让周期中途死路；fail-closed 归一化为 Hold 走收尾边，
        // 绝不放行未路由的越界提案（Hold=0 是有意的安全零值，属合法动作，只有越界值非法）。
        if (!Enum.IsDefined(proposal.Action))
        {
            ctx.SetOutcome(new CycleOutcome("failed", "stage_contract_violation",
                "交易员提案动作越界，本周期已按失败安全终止"));
            var safe = proposal with { Action = ProposalAction.Hold };
            ctx.Observe(safe);
            await ctx.Recorder.RecordStageAsync(Stage, new ProposalMessage(safe),
                PromptLibrary.Build(AgentRole.Trader, ctx.Config), ct);
            return new ProposalMessage(safe);
        }

        ctx.Observe(proposal);
        await ctx.Recorder.RecordStageAsync(Stage, new ProposalMessage(proposal),
            PromptLibrary.Build(AgentRole.Trader, ctx.Config), ct);

        if (proposal.Action == ProposalAction.Hold)
        {
            ctx.SetOutcome(new CycleOutcome("completed", null, null));
        }

        return new ProposalMessage(proposal);
    }
}
