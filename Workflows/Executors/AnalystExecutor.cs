using System.Diagnostics;
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
        // 输入 token 预算闸（LC-STD-01 §6、FLOW-09）：超限不再发起模型调用，
        // 空候选出边直接收尾，终态记「观望完成」；不调模型、不产生本阶段记录。
        if (ctx.TokenBudgetExhausted())
        {
            ctx.SetOutcome(new CycleOutcome("completed", "token_budget_exceeded",
                "输入 token 预算已达上限，后续阶段停止，本周期观望"));
            return new ResearchBriefMessage(
                new Contracts.ResearchBrief("输入 token 预算已达上限，本周期观望", [], [], []));
        }

        var agent = factory.Create(client, AgentRole.Analyst, ctx.Config,
            tools.For(AgentRole.Analyst, StageRunId), null);
        var prompt = await ctx.BuildUserPrompt(AgentRole.Analyst, ct);
        // 阶段超时（LC-STD-01 §6）：超时按 CancellationToken 传播到模型与工具循环，阶段按失败处理。
        using var stageTimeout = ctx.StageTimeout(ct, Stage);
        var started = Stopwatch.StartNew();
        AgentRunResult<Contracts.ResearchBrief> result;
        try
        {
            result = await runner.RunAsync<Contracts.ResearchBrief>(agent, prompt, stageTimeout.Token);
        }
        catch (OperationCanceledException) when (stageTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 仅阶段自身超时写入计时；用户停止/周期超时的取消不得伪装成阶段超时。
            ctx.MarkStageTimeout(Stage, started.ElapsedMilliseconds);
            throw;
        }

        var brief = result.Output;
        var stats = StageRunStats.From(result.Messages, result.Usage, started.ElapsedMilliseconds);

        // 分析师已拿到实际候选：按真实标的数抬高交易员/后续阶段额度（纯计数实现为空操作）。
        ctx.Budget.ApplySymbolScaling(brief.Candidates.Count);

        ctx.Observe(brief);
        await ctx.Recorder.RecordStageAsync(Stage, StageRunId, brief,
            PromptLibrary.Build(AgentRole.Analyst, ctx.Config), stats, 1, ct);

        if (brief.Candidates.Count == 0)
        {
            ctx.SetOutcome(new CycleOutcome("skipped", null, null));
        }

        return new ResearchBriefMessage(brief);
    }
}
