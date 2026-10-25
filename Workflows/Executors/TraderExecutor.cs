using System.Diagnostics;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Memory;
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
        // 输入 token 预算闸（LC-STD-01 §6、FLOW-09）：超限不再发起模型调用；
        // Hold 出边直接收尾，观望完成、不产生提案。
        if (ctx.TokenBudgetExhausted())
        {
            ctx.SetOutcome(new CycleOutcome("completed", "token_budget_exceeded",
                "输入 token 预算已达上限，后续阶段停止，本周期观望"));
            return new ProposalMessage(new AgentTradeProposal(ProposalAction.Hold, null, null, null, null,
                null, null, null, null, "输入 token 预算已达上限，本周期观望", null, null));
        }

        // 记忆与经验只注入交易员（LC-STD-04 §2 块 6）：读取器取本周期装配位的 ctx.Accounts，
        // 试运行的 includeMemory 门禁已在该装配位短路；开关关闭时提供器不读库也不注入。
        var agent = factory.Create(client, AgentRole.Trader, ctx.Config,
            tools.For(AgentRole.Trader, StageRunId), [new TradingMemoryContextProvider(ctx.Accounts, ctx.Config)]);
        var prompt = await PromptLibrary.BuildTraderUserPrompt(ctx, input.Brief, ct);
        // 阶段超时（LC-STD-01 §6）：超时按 CancellationToken 传播到模型与工具循环，阶段按失败处理。
        using var stageTimeout = ctx.StageTimeout(ct, Stage);
        var started = Stopwatch.StartNew();
        var attempt = 1;
        AgentRunResult<AgentTradeProposal> result;
        try
        {
            result = await runner.RunAsync<AgentTradeProposal>(agent, prompt, stageTimeout.Token);
        }
        catch (AgentOutputFormatException)
        {
            // 输出不是合法 JSON 契约：与越界动作同属契约失败，走 FLOW-07 的同一次重试
            result = null!;
        }

        // FLOW-07（LC-STD-05）：交易员契约失败先重试 1 次——同一提示词、同一阶段超时窗口内再跑一轮
        // （不新起阶段，模型调用消耗仍归本阶段预算）；重试得到合法动作则照常继续。
        if (result is null || !Enum.IsDefined(result.Output.Action))
        {
            attempt = 2;
            result = await runner.RunAsync<AgentTradeProposal>(agent, prompt, stageTimeout.Token);
        }

        var proposal = result.Output;
        var stats = StageRunStats.From(result.Messages, result.Usage, started.ElapsedMilliseconds);

        // 契约校验（LC-STD-01 §4.1 第 3 步）：System.Text.Json 会原样接受越界枚举值，
        // 未定义动作匹配不到任何出边会让周期中途死路；重试后仍越界才 fail-closed，
        // 归一化为 Hold 走收尾边，绝不放行未路由的越界提案（Hold=0 是有意的安全零值，属合法动作，只有越界值非法）。
        if (!Enum.IsDefined(proposal.Action))
        {
            ctx.SetOutcome(new CycleOutcome("failed", "stage_contract_violation",
                "交易员提案动作越界（重试 1 次仍失败），本周期已按失败安全终止"));
            var safe = proposal with { Action = ProposalAction.Hold };
            ctx.Observe(safe);
            await ctx.Recorder.RecordStageAsync(Stage, StageRunId, new ProposalMessage(safe),
                PromptLibrary.Build(AgentRole.Trader, ctx.Config), stats, attempt, ct);
            return new ProposalMessage(safe);
        }

        ctx.Observe(proposal);
        await ctx.Recorder.RecordStageAsync(Stage, StageRunId, new ProposalMessage(proposal),
            PromptLibrary.Build(AgentRole.Trader, ctx.Config), stats, attempt, ct);

        if (proposal.Action == ProposalAction.Hold)
        {
            ctx.SetOutcome(new CycleOutcome("completed", null, null));
        }

        return new ProposalMessage(proposal);
    }
}
