using LuckyCat.Contracts;
using LuckyCat.Models;
using LuckyCat.Tools;

namespace LuckyCat.Services;

/// <summary>
/// 单周期预算（LC-STD-01 §6）：工具调用按角色与按周期计数、token 用量累计、超时配置读取。
/// 超限一律返回 false（不抛异常）：调用方（<see cref="ToolAuditor"/>）据此回
/// <c>tool_budget_exceeded</c> 信封，模型可在同一轮内继续收敛输出。
/// 一个实例只服务一个周期：随周期创建，不静态共享（LC-STD-01 §4.2）。
/// </summary>
public sealed class CycleBudget(AgentBudgetConfig config) : ICycleBudget
{
    /// <summary>MaxOutputTokens 未配置（&lt;=0）时的默认输出上限（LC-STD-01 §6）。</summary>
    public const int DefaultMaxOutputTokens = 16_384;

    /// <summary>输入 token 预算上界与下限（LC-STD-01 §6）。</summary>
    public const int MaxInputTokenBudget = 150_000;

    public const int MinInputTokenBudget = 1_000;

    private readonly Dictionary<string, int> _toolsByRole = new(StringComparer.Ordinal);

    /// <summary>本周期已消耗的工具调用总数（含被拒绝的调用）。</summary>
    public int TotalToolCalls { get; private set; }

    /// <summary>本周期累计输入 token。</summary>
    public int InputTokens { get; private set; }

    /// <summary>本周期累计输出 token。</summary>
    public int OutputTokens { get; private set; }

    /// <summary>单周期总超时（秒）。</summary>
    public int CycleTimeoutSeconds => config.CycleTimeoutSeconds;

    /// <summary>指定阶段配置的超时（秒）；未知阶段取交易员阶段口径。</summary>
    public int StageTimeoutSeconds(string stage) => stage switch
    {
        "analyst" => config.AnalystTimeoutSeconds,
        "risk" => config.RiskTimeoutSeconds,
        "executor" => config.ExecutorTimeoutSeconds,
        _ => config.TraderTimeoutSeconds,
    };

    /// <summary>该角色的工具上限；未列出的角色（如复盘员）只受周期总量约束。</summary>
    public int RoleToolLimit(string role) => role switch
    {
        "analyst" => config.AnalystToolLimit,
        "trader" => config.TraderToolLimit,
        "risk" => config.RiskToolLimit,
        "executor" => config.ExecutorToolLimit,
        _ => int.MaxValue,
    };

    /// <summary>该角色已消耗的工具调用数。</summary>
    public int ToolCalls(string role) => _toolsByRole.GetValueOrDefault(role);

    /// <summary>
    /// 消耗一次工具额度：角色超限或周期总量超限时返回 false 且不计数（不抛异常）。
    /// </summary>
    public bool TryConsumeTool(string role)
    {
        if (TotalToolCalls >= config.CycleToolLimit) return false;
        if (ToolCalls(role) >= RoleToolLimit(role)) return false;
        _toolsByRole[role] = ToolCalls(role) + 1;
        TotalToolCalls++;
        return true;
    }

    /// <summary>累计一次模型调用的 token 用量（上游未回报时按 null 忽略）。</summary>
    public void AddTokens(int? inputTokens, int? outputTokens)
    {
        InputTokens = SaturateAdd(InputTokens, inputTokens);
        OutputTokens = SaturateAdd(OutputTokens, outputTokens);
    }

    /// <summary>本周期输入 token 预算（公式见 <see cref="ComputeInputTokenBudget"/>）。</summary>
    public int InputTokenBudget(Provider provider) => ComputeInputTokenBudget(config, provider);

    /// <summary>
    /// 预算在每次模型调用前检查：已用量达到预算即视为超限，宿主不再发起后续调用
    /// （LC-STD-01 §6「超限不再发起调用」；LC-STD-05 FLOW-09）。
    /// </summary>
    public bool IsInputTokenBudgetExceeded(Provider provider) => InputTokens >= InputTokenBudget(provider);

    /// <summary>
    /// 输入 token 预算 = min(配置上限 150,000, Provider.ContextTokens − effectiveMaxOutputTokens)，下限 1,000；
    /// effectiveMaxOutputTokens = MaxOutputTokens &gt; 0 ? MaxOutputTokens : 16,384；
    /// ContextTokens = 0 时取配置上限（LC-STD-01 §6）。
    /// </summary>
    public static int ComputeInputTokenBudget(AgentBudgetConfig config, Provider provider)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(provider);

        var ceiling = Math.Clamp(config.InputTokenBudget > 0 ? config.InputTokenBudget : MaxInputTokenBudget,
            MinInputTokenBudget, MaxInputTokenBudget);
        if (provider.ContextTokens <= 0) return ceiling;
        var effectiveMaxOutput = provider.MaxOutputTokens > 0 ? provider.MaxOutputTokens : DefaultMaxOutputTokens;
        return Math.Clamp(Math.Min(ceiling, provider.ContextTokens - effectiveMaxOutput),
            MinInputTokenBudget, ceiling);
    }

    private static int SaturateAdd(int current, int? delta) => delta is not { } value
        ? current
        : value > 0 && current > int.MaxValue - value
            ? int.MaxValue
            : current + Math.Max(value, 0);
}
