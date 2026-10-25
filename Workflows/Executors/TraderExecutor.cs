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
