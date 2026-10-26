using LuckyCat.Contracts;
using LuckyCat.Workflows.Executors;
using Microsoft.Agents.AI.Workflows;

namespace LuckyCat.Workflows;

/// <summary>
/// 四段交易图（LC-STD-01 §4）：Analyst → Trader →（open）Risk → Executor，
/// close 跳过风控直达 Executor，hold / 无候选 / 风控拒绝直达 Finalizer；
/// 所有终态路径汇聚 Finalizer 并以其 CycleSignal 作为图输出。六条条件边对应
/// LC-STD-05 FLOW-01~05、FLOW-14，谓词只读消息本体，不引入额外状态。
/// </summary>
public static class TradingCycleWorkflow
{
    public static Workflow Build(TradingCycleContext ctx, AnalystExecutor analyst,
        TraderExecutor trader, RiskExecutor risk, ExecutionExecutor executor, FinalizerExecutor finalizer)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // 谓词参数在框架标注中可空；图内消息永不为 null，用 null-forgiving 保持谓词纯读取。
        return new WorkflowBuilder(analyst)
            .AddEdge<ResearchBriefMessage>(analyst, trader, m => m!.Brief.Candidates.Count > 0)
            .AddEdge<ResearchBriefMessage>(analyst, finalizer, m => m!.Brief.Candidates.Count == 0)
            .AddEdge<ProposalMessage>(trader, risk, m => m!.Proposal.Action == ProposalAction.Open)
            .AddEdge<ProposalMessage>(trader, executor, m => m!.Proposal.Action == ProposalAction.Close)
            .AddEdge<ProposalMessage>(trader, finalizer, m => m!.Proposal.Action == ProposalAction.Hold)
            .AddEdge<VerdictMessage>(risk, executor, m => m!.Verdict.Decision == RiskDecision.Approved)
            .AddEdge<VerdictMessage>(risk, finalizer, m => m!.Verdict.Decision == RiskDecision.Rejected)
            .AddEdge<ExecutionReport>(executor, finalizer, _ => true)
            .WithOutputFrom(finalizer)
            .Build();
    }
}
