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

    /// <summary>
    /// 复盘单次超时（秒）与工具上限（LC-STD-01 §9「限制」行的字面值）。
    /// 二者是复盘任务的<b>硬限制</b>，刻意不取策略配置的 <see cref="AgentBudgetConfig"/>：
    /// 交易预算改配置不能改回复盘口径（R-13-6）。
    /// </summary>
    public const int ReviewTimeoutSeconds = 120;

    public const int ReviewToolLimit = 10;

    private readonly Dictionary<string, int> _toolsByRole = new(StringComparer.Ordinal);

    /// <summary>本周期已消耗的工具调用总数（含被拒绝的调用）。</summary>
    public int TotalToolCalls { get; private set; }

    /// <summary>本周期累计输入 token。</summary>
    public int InputTokens { get; private set; }

    /// <summary>本周期累计输出 token。</summary>
    public int OutputTokens { get; private set; }

    /// <summary>
    /// 被预算拒绝的工具调用次数（12/8/4/6/10 与周期总量任一上限命中都计数）。
    /// 复盘宿主用它把「工具上限耗尽」判定为失败（LC-STD-01 §9 失败行），而不是让模型用
    /// 一个 <c>tool_budget_exceeded</c> 信封继续拼出一份经验。
    /// </summary>
    public int ToolRefusals { get; private set; }

    /// <summary>单周期总超时（秒）。</summary>
    public int CycleTimeoutSeconds => config.CycleTimeoutSeconds;

    /// <summary>指定阶段配置的超时（秒）；未知阶段取交易员阶段口径；复盘阶段取 §9 的硬限制。</summary>
    public int StageTimeoutSeconds(string stage) => stage switch
    {
        "analyst" => config.AnalystTimeoutSeconds,
        "risk" => config.RiskTimeoutSeconds,
        "executor" => config.ExecutorTimeoutSeconds,
        "reviewer" => ReviewTimeoutSeconds,
        _ => config.TraderTimeoutSeconds,
    };

    /// <summary>该角色的工具上限；未列出的角色（除复盘员）只受周期总量约束。</summary>
    public int RoleToolLimit(string role) => role switch
    {
        "analyst" => config.AnalystToolLimit,
        "trader" => config.TraderToolLimit,
        "risk" => config.RiskToolLimit,
        "executor" => config.ExecutorToolLimit,
        "reviewer" => ReviewToolLimit,
        _ => int.MaxValue,
    };

    /// <summary>该角色已消耗的工具调用数。</summary>
    public int ToolCalls(string role) => _toolsByRole.GetValueOrDefault(role);

    /// <summary>
    /// 消耗一次工具额度：角色超限或周期总量超限时返回 false 且不计数（不抛异常）。
    /// </summary>
    public bool TryConsumeTool(string role)
    {
        if (TotalToolCalls >= config.CycleToolLimit || ToolCalls(role) >= RoleToolLimit(role))
        {
            ToolRefusals++;
            return false;
        }

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
    /// 复盘专用预算实例（LC-STD-01 §9「单次超时 120s、工具上限 10」）：复盘不是周期，
    /// 因此不复用任何交易周期的计数状态，但复用同一套 <see cref="ICycleBudget"/> 机制
    /// （R-13-6：不另造一套预算）。上限取 <see cref="ReviewToolLimit"/> 常量，
    /// 与策略配置里的交易口径无关。
    /// </summary>
    public static CycleBudget ForReview() => new(new AgentBudgetConfig
    {
        AnalystToolLimit = ReviewToolLimit,
        TraderToolLimit = ReviewToolLimit,
        RiskToolLimit = ReviewToolLimit,
        ExecutorToolLimit = ReviewToolLimit,
        CycleToolLimit = ReviewToolLimit,
        CycleTimeoutSeconds = ReviewTimeoutSeconds,
        AnalystTimeoutSeconds = ReviewTimeoutSeconds,
        TraderTimeoutSeconds = ReviewTimeoutSeconds,
        RiskTimeoutSeconds = ReviewTimeoutSeconds,
        ExecutorTimeoutSeconds = ReviewTimeoutSeconds,
    });

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
