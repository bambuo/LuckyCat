using System.Globalization;
using LuckyCat.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LuckyCat.Agents;

/// <summary>
/// 一次复盘任务的输入契约（LC-STD-01 §9「窗口」行）：所属交易员、数据窗口与窗口内的数据量观测。
/// 计数由复盘宿主在取数探针阶段得到，只用于提示词的可读上下文，不是模型的取数上限；
/// 时间一律 UTC，格式一律 RFC 3339（<c>O</c> 格式），与工具入参 <c>sinceUtc</c> 同一口径。
/// </summary>
/// <param name="TraderId">复盘所属交易员（写入 <c>AgentStageRun.traderId</c>）。</param>
/// <param name="TraderName">交易员名称，仅用于提示词归属声明。</param>
/// <param name="PeriodStartUtc">窗口起点 = 上次成功复盘之后。</param>
/// <param name="PeriodEndUtc">窗口终点 = 本轮复盘开始时点。</param>
/// <param name="ClosedTradeCount">窗口内已平仓交易笔数（宿主探针结果）。</param>
/// <param name="CycleCount">窗口内周期数（宿主探针结果）。</param>
/// <param name="LessonCount">该交易员历史经验份数（供模型判断是否重复既有规则）。</param>
public sealed record ReviewWindow(
    int TraderId,
    string TraderName,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    int ClosedTradeCount,
    int CycleCount,
    int LessonCount);

/// <summary>
/// 复盘员装配位（LC-STD-01 §3 角色表 + §9 复盘任务表、LC-STD-04 §3.5）：
/// 与四个交易角色共用 <see cref="TradingAgentFactory"/> 与 <see cref="PromptLibrary"/>，
/// 只有两处刻意的差别：
/// ① <b>不接任何上下文提供器</b>（R-13-8；LC-STD-04 §2 块 6「记忆与经验仅交易员注入」）——
/// 复盘员既拿不到提案记忆，也拿不到经验注入块；§9「输入」里的「已有经验」由它自己的
/// <c>get_lessons</c> 只读工具按需取，因此复盘员的系统指令里没有记忆块，请求里也没有注入的 system 消息。
/// ② 工具集只来自 <see cref="ToolRegistry.BuildReviewerTools"/>（唯一装配位，清单唯一来源
/// <see cref="ToolCatalog.Reviewer"/>），复盘侧不得另起第二张工具清单。
/// <para>
/// 复盘不是交易周期，因此这里没有 <c>TradingCycleContext</c>：宿主（Task 13 的 <c>ReviewRunner</c>）
/// 自带 <c>cycleId=null</c> 的 <see cref="ToolAuditor"/>（LC-STD-03 §2.1「复盘工具调用 cycleId 为 null」），
/// 本类只负责把「指令 + 工具 + 无提供器」装配成 <see cref="ChatClientAgent"/>。
/// </para>
/// </summary>
public static class ReviewerAgent
{
    /// <summary>
    /// 装配复盘员：系统指令取 <see cref="PromptLibrary.Build"/>（唯一入口，与落库的
    /// <c>AgentStageRun.instructions</c> 逐字一致），上下文提供器一律为 null。
    /// </summary>
    public static ChatClientAgent New(IChatClient client, StrategyConfig config, IList<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tools);

        // providers 传 null：工厂据此装配空提供器集合（R-13-8 / LC-STD-04 §2 块 6 交易员专属）。
        return new TradingAgentFactory().Create(client, AgentRole.Reviewer, config, tools, providers: null);
    }

    /// <summary>
    /// 复盘员用户提示词（LC-STD-04 §2 最小必要）：只交给模型「本轮窗口 + 归属 + 取数口径」，
    /// 具体数据一律由只读工具自取——复盘输入是窗口内数据，不是宿主拼好的大段历史正文
    /// （§9「限制」行：只读工具、单次超时 120s、工具上限 10）。
    /// <para>
    /// 窗口起点必须逐字出现在 <c>窗口起点 </c> 之后的独立一行：模型的 <c>get_closed_trades</c> /
    /// <c>get_cycles</c> 调用要把该值原样作为 <c>sinceUtc</c> 传入，否则工具默认只查最近 7 天，
    /// 与 §9「上次成功复盘之后、最多 30 天」的窗口口径不符。
    /// </para>
    /// </summary>
    public static string BuildUserPrompt(ReviewWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var sb = new System.Text.StringBuilder();
        sb.Append("本轮复盘任务（交易员：").Append(window.TraderName)
            .Append(" #").Append(window.TraderId.ToString(CultureInfo.InvariantCulture)).Append("）：\n");
        sb.Append("复盘窗口起点 ").Append(Time(window.PeriodStartUtc)).Append('\n');
        sb.Append("复盘窗口终点 ").Append(Time(window.PeriodEndUtc)).Append('\n');
        sb.Append("- 窗口内已平仓交易 ").Append(count(window.ClosedTradeCount))
            .Append(" 笔；周期 ").Append(count(window.CycleCount))
            .Append(" 个；该交易员历史经验 ").Append(count(window.LessonCount)).Append(" 份。\n");
        sb.Append("请先用只读工具按上面的窗口起点取数（get_closed_trades 与 get_cycles 的 sinceUtc ")
            .Append("传窗口起点值），核对后再输出经验；窗口之外的一律不取、不引。\n");
        sb.Append("结论必须引用工具返回的订单/周期 ID，区分运气与流程；单次盈亏不得写成规律。\n");
        sb.Append("你是只读复盘角色：不得修改策略、风控配置、交易员状态或历史交易记录，")
            .Append("不得给出未来价格预测，不得下单，不得调用任何写操作。");
        return sb.ToString();

        static string count(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Time(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
}
