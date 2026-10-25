using LuckyCat.Agents;
using LuckyCat.Data;
using LuckyCat.Services;
using LuckyCat.Workflows;
using Microsoft.Extensions.AI;

namespace LuckyCat.Tools;

/// <summary>
/// 角色工具注册位：把 Task 5/6/8 的 <see cref="MarketTools"/>、<see cref="AccountTools"/>、
/// <see cref="ExecutionTools"/> 方法按 <see cref="ToolCatalog"/> 的角色清单装配为 <see cref="AITool"/>。
/// 工具名逐字取自清单数组（不允许改名/新增/遗漏，测试逐字节比对）；只读角色只拿读工具，
/// 执行员独享写工具；每次装配绑定一个阶段级 <see cref="ToolAuditor"/>（落 ToolInvocation、
/// 消耗周期工具预算、发布 tool SSE）。
/// </summary>
public sealed class ToolRegistry(
    TradingCycleContext cycle,
    AppDbContext db,
    IExecutionGateway gateway,
    SseEventHub hub,
    IReadOnlySet<string> allowedSymbols)
{
    /// <summary>返回指定角色在指定阶段可用的工具集（顺序与 <see cref="ToolCatalog"/> 一致）。</summary>
    public IList<AITool> For(AgentRole role, string stageRunId)
    {
        // 频道单点决策（R-11-7）：审计器与写工具都发到 cycle.SseChannel，试运行不再落 trader:0。
        var audit = new ToolAuditor(db, hub, cycle.Budget, cycle.CycleId, stageRunId, cycle.SseChannel);
        return role switch
        {
            AgentRole.Analyst => AnalystTools(audit),
            AgentRole.Trader => TraderTools(audit),
            AgentRole.Risk => RiskTools(audit),
            AgentRole.Executor => ExecutorTools(audit, stageRunId),
            // 结构性禁止（LC-STD-03 §2.1「复盘工具调用 cycleId 为 null」）：For 会把周期的
            // 非空 cycleId 写进审计器，复盘员走这条路径必然违规，因此在注册位直接拒绝，
            // 唯一装配入口是静态的 BuildReviewerTools（自带 cycleId=null 审计器）。
            AgentRole.Reviewer => throw new InvalidOperationException(
                "复盘员工具不能走周期装配位 For（会带上非空 cycleId，违反 LC-STD-03 §2.1）；" +
                $"唯一入口是 {nameof(BuildReviewerTools)}。"),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown agent role."),
        };
    }

    // 分析师：仅行情读工具（不注入账户数据，LC-STD-01 §3 / LC-STD-04 §3.1）。
    private IList<AITool> AnalystTools(ToolAuditor audit)
    {
        var market = new MarketTools(cycle.Market, cycle.Config, allowedSymbols, audit, RoleNames.Analyst);
        return
        [
            Fn(market.GetCoinCandidatesAsync, ToolCatalog.Analyst[0]),
            Fn(market.GetMarketOverviewAsync, ToolCatalog.Analyst[1]),
            Fn(market.GetKlinesAsync, ToolCatalog.Analyst[2]),
            Fn(market.GetIndicatorsAsync, ToolCatalog.Analyst[3]),
            Fn(market.GetSentimentAsync, ToolCatalog.Analyst[4]),
            Fn(market.GetNewsAsync, ToolCatalog.Analyst[5]),
        ];
    }

    // 交易员：行情读 + 账户/持仓/订单/记忆/经验/纪律读。
    private IList<AITool> TraderTools(ToolAuditor audit)
    {
        var market = new MarketTools(cycle.Market, cycle.Config, allowedSymbols, audit, RoleNames.Trader);
        var account = new AccountTools(cycle.Accounts, audit, RoleNames.Trader);
        var names = ToolCatalog.Trader;
        return
        [
            Fn(market.GetKlinesAsync, names[0]),
            Fn(market.GetIndicatorsAsync, names[1]),
            Fn(market.GetSentimentAsync, names[2]),
            Fn(account.GetAccountAsync, names[3]),
            Fn(account.GetPositionsAsync, names[4]),
            Fn(account.GetRecentOrdersAsync, names[5]),
            Fn(account.GetClosedTradesAsync, names[6]),
            Fn(account.GetDecisionMemoryAsync, names[7]),
            Fn(account.GetLessonsAsync, names[8]),
            Fn(account.GetTradingDisciplineAsync, names[9]),
        ];
    }

    // 风控官：账户/持仓/风控配置/纪律读。
    private IList<AITool> RiskTools(ToolAuditor audit)
    {
        var account = new AccountTools(cycle.Accounts, audit, RoleNames.Risk);
        var names = ToolCatalog.Risk;
        return
        [
            Fn(account.GetAccountAsync, names[0]),
            Fn(account.GetPositionsAsync, names[1]),
            Fn(account.GetRiskPolicyAsync, names[2]),
            Fn(account.GetTradingDisciplineAsync, names[3]),
        ];
    }

    // 执行员：账户读三件 + 三件写工具（写工具绑定本阶段的 ExecutionToolContext 与审计器）。
    private IList<AITool> ExecutorTools(ToolAuditor audit, string stageRunId)
    {
        var account = new AccountTools(cycle.Accounts, audit, RoleNames.Executor);
        var execution = new ExecutionTools(db, cycle.ForStage(stageRunId), cycle.Accounts,
            gateway, cycle.Market, hub, audit);
        var names = ToolCatalog.Executor;
        return
        [
            Fn(account.GetAccountAsync, names[0]),
            Fn(account.GetPositionsAsync, names[1]),
            Fn(account.GetRecentOrdersAsync, names[2]),
            Fn(execution.PlaceOrderAsync, names[3]),
            Fn(execution.ClosePositionAsync, names[4]),
            Fn(execution.CancelOrderAsync, names[5]),
        ];
    }

    // 复盘员没有注册位：复盘链路不存在交易周期，For(AgentRole.Reviewer) 直接被拒绝（见上），
    // 工具集只经下方静态入口 BuildReviewerTools 装配。

    /// <summary>
    /// 复盘员工具集的<b>唯一</b>装配位（LC-STD-01 §9 输入行 = <see cref="ToolCatalog.Reviewer"/> 三件读工具）。
    /// <para>
    /// 单独暴露静态入口的原因：复盘链路<b>没有交易周期</b>，因此不能走 <see cref="For"/>——
    /// 那条路径把 <see cref="TradingCycleContext.CycleId"/>（非空）写进 ToolAuditor，
    /// 而 LC-STD-03 §2.1 明文「复盘工具调用 <c>cycleId</c> 为 null」。本入口由复盘宿主自带
    /// <c>cycleId=null</c> 的审计器调用，工具清单仍然只有一份，不在复盘侧另起第二张清单。
    /// </para>
    /// </summary>
    public static IList<AITool> BuildReviewerTools(IAccountReader accounts, ToolAuditor audit)
    {
        var account = new AccountTools(accounts, audit, RoleNames.Reviewer);
        var names = ToolCatalog.Reviewer;
        return
        [
            Fn(account.GetClosedTradesAsync, names[0]),
            Fn(account.GetCyclesAsync, names[1]),
            Fn(account.GetLessonsAsync, names[2]),
        ];
    }

    /// <summary>审计与预算计数的角色名（复盘宿主装配审计器时用同一字面量）。</summary>
    internal const string ReviewerRoleName = RoleNames.Reviewer;

    // 名称逐字来自 ToolCatalog；描述沿用方法上的 [Description]（DescriptionAttribute）。
    private static AIFunction Fn(Delegate method, string name) =>
        AIFunctionFactory.Create(method, name);

    /// <summary>审计与预算计数使用的角色名（与 ToolCatalog 角色一致的小写串）。</summary>
    private static class RoleNames
    {
        public const string Analyst = "analyst";
        public const string Trader = "trader";
        public const string Risk = "risk";
        public const string Executor = "executor";
        public const string Reviewer = "reviewer";
    }
}
