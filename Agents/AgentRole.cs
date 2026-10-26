namespace LuckyCat.Agents;

/// <summary>
/// 多智能体工作流中的固定角色。角色名会写入 <c>ChatClientAgent.Name</c> 与阶段日志。
/// </summary>
public enum AgentRole
{
    Analyst,
    Trader,
    Risk,
    Executor,
    Reviewer,
}
