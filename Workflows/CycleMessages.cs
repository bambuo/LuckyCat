using LuckyCat.Contracts;

namespace LuckyCat.Workflows;

// 四段图的消息契约（LC-STD-01 §4）：每个阶段消费上一条消息、产出下一条消息；
// 终态由 CycleOutcome 表达、由 Finalizer 统一转为 CycleSignal。记录类型不可变。

/// <summary>图入口：仅携带全链路关联键 CycleId。</summary>
public sealed record CycleInput(string CycleId);

/// <summary>分析师产出：研究简报；candidates 为空走「无候选」终态路由。</summary>
public sealed record ResearchBriefMessage(ResearchBrief Brief);

/// <summary>交易员产出：交易提案；Action 决定 open/close/hold 三条路由。</summary>
public sealed record ProposalMessage(AgentTradeProposal Proposal);

/// <summary>风控官产出：开仓提案 + 裁决（仅 open 产生本消息）。</summary>
public sealed record VerdictMessage(AgentTradeProposal Proposal, AgentRiskVerdict Verdict);

/// <summary>终态信号：Finalizer 输出，Reason 为周期状态（completed/skipped/failed）。</summary>
public sealed record CycleSignal(string Reason);

/// <summary>
/// 周期终态（阶段写入、Finalizer 读取）。状态语义见 LC-STD-01 §7：
/// 无候选 skipped；hold/风控拒绝/写工具拒绝/执行成功均 completed；写工具拒绝时
/// ErrorCode=execution_errors 且 Error 为报告错误摘要（completed 但 errors 非空）。
/// </summary>
public sealed record CycleOutcome(string Status, string? ErrorCode, string? Error);
