using LuckyCat.Agents;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Workflows;

namespace LuckyCat.Workflows.Executors;

/// <summary>
/// 分析师阶段：读取候选池与行情工具，产出研究简报；candidates 为空时写入 skipped 终态
/// 并走「无候选」条件边（FLOW-01）。指令原文与 PromptLibrary 完全一致（LC-STD-04 §2）。
/// </summary>
public sealed class AnalystExecutor(TradingCycleContext ctx, TradingAgentFactory factory,
    AgentRunner runner, ToolRegistry tools, IChatClient client)
    : Executor<CycleInput, ResearchBriefMessage>("analyst"), IStageExecutor
{
    public string Stage => "analyst";

    /// <summary>本执行器实例绑定的阶段运行 ID：ToolAuditor 落 ToolInvocation 用同一个 ID，
    /// 周期宿主（Task 11）可据此把工具审计行与 AgentStageRun 对齐。</summary>
    public string StageRunId { get; } = Ids.NewUlid();

    public override async ValueTask<ResearchBriefMessage> HandleAsync(
        CycleInput input, IWorkflowContext context, CancellationToken ct)
    {
        var agent = factory.Create(client, AgentRole.Analyst, ctx.Config,
            tools.For(AgentRole.Analyst, StageRunId), null);
        var prompt = await ctx.BuildUserPrompt(AgentRole.Analyst, ct);
        var (brief, _) = await runner.RunAsync<Contracts.ResearchBrief>(agent, prompt, ct);

        ctx.Observe(brief);
        await ctx.Recorder.RecordStageAsync(Stage, brief,
            PromptLibrary.Build(AgentRole.Analyst, ctx.Config), ct);

        if (brief.Candidates.Count == 0)
        {
            ctx.SetOutcome(new CycleOutcome("skipped", null, null));
        }

        return new ResearchBriefMessage(brief);
    }
}
