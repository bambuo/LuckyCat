namespace LuckyCat.Workflows.Executors;

/// <summary>
/// 阶段执行器抽象：图中五个执行器（分析师/交易员/风控/执行/收尾）都实现本接口，
/// Stage 为审计与测试观察「哪些阶段被调用」的稳定标识；四个 Agent 阶段与角色名一致，
/// 收尾阶段固定为 finalizer（不调用模型、不产生 AgentStageRun，LC-STD-01 §4.1）。
/// </summary>
public interface IStageExecutor
{
    string Stage { get; }
}
