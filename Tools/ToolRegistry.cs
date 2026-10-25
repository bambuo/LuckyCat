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
        var audit = new ToolAuditor(db, hub, cycle.Budget, cycle.CycleId, stageRunId, cycle.Trader.Id);
        return role switch
        {
            AgentRole.Analyst => AnalystTools(audit),
            AgentRole.Trader => TraderTools(audit),
            AgentRole.Risk => RiskTools(audit),
            AgentRole.Executor => ExecutorTools(audit, stageRunId),
            AgentRole.Reviewer => ReviewerTools(audit),
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

    // 复盘员（Task 13 的注册位）：已平仓交易/周期/经验读。
    private IList<AITool> ReviewerTools(ToolAuditor audit)
    {
        var account = new AccountTools(cycle.Accounts, audit, RoleNames.Reviewer);
        var names = ToolCatalog.Reviewer;
        return
        [
            Fn(account.GetClosedTradesAsync, names[0]),
            Fn(account.GetCyclesAsync, names[1]),
            Fn(account.GetLessonsAsync, names[2]),
        ];
    }

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
