using System.Text.Json;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// 智能体提案 → 旧版风控契约的适配层：
/// ① 动作：open 按方向映射 buy/sell（方向缺失时降级为 hold，由 <see cref="ValidateOpen"/> 拒绝）；
/// ② 信心度：提案为 0–1，旧契约要求 0–100 整数，此处向下取整放大 100 倍（绝不向上放大提案信心）；
/// ③ 止损/止盈：提案为绝对价格，旧契约要求相对入场价的百分点，此处按 |入场价-保护价|/入场价 换算；
///    方向合法性（多头止损在下、止盈在上，空头相反）由 <see cref="ValidateOpen"/> 在下单前校验；
/// ④ close/hold 不携带开仓专属字段，避免旧契约把非开仓提案误判为可执行信号。
/// 实体重载（<see cref="LuckyCat.Models.TradeProposal"/>）供写工具直接复用，避免调用方各写一遍换算；
/// <see cref="ToContract"/> 对非法枚举/JSON 降级为安全的 Hold 契约，严格校验路径为 <see cref="TryToContract"/>。
/// </summary>
public static class ProposalRiskAdapter
{
    public static JsonElement ToLegacyDecision(AgentTradeProposal p)
    {
        var action = p.Action switch
        {
            ProposalAction.Open => p.Side switch
            {
                TradeSide.Long => "buy",
                TradeSide.Short => "sell",
                _ => "hold" // 方向缺失不得默认按空头开仓；ValidateOpen 会返回契约错误。
            },
            ProposalAction.Close => "close",
            _ => "hold"
        };
        var isOpen = action is "buy" or "sell";
        decimal? stopPct = isOpen && p.EntryPrice is > 0 && p.StopLossPrice is { } sl
            ? Math.Abs(p.EntryPrice.Value - sl) / p.EntryPrice.Value * 100m : null;
        decimal? takePct = isOpen && p.EntryPrice is > 0 && p.TakeProfitPrice is { } tp
            ? Math.Abs(tp - p.EntryPrice.Value) / p.EntryPrice.Value * 100m : null;
        var obj = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["symbol"] = p.Symbol,
            // 向下取整：0.729 → 72，不让适配层把提案信心抬高到 73。
            ["confidence"] = p.Confidence is { } c ? Math.Floor(c * 100m) : null,
            ["reason"] = p.Thesis,
            ["entry_price"] = isOpen ? p.EntryPrice : null,
            ["stop_loss_pct"] = stopPct,
            ["take_profit_pct"] = takePct,
            ["position_pct"] = isOpen ? p.PositionPct : null,
            ["leverage"] = isOpen ? p.Leverage : null,
            ["trigger"] = action == "hold" ? "hold" : null
        };
        return JsonSerializer.SerializeToElement(obj);
    }

    /// <summary>
    /// 仅开仓提案进入旧版风控；close/hold 直接拒绝（不会伪造可执行计划）。
    /// 开仓必须给出方向与双向保护价，且保护价必须在入场价正确一侧——旧策略的 Math.Abs 换算
    /// 会把「多单止损在高位/空单止盈在高位」这类错误方向洗成合法百分比，必须在适配层拦下。
    /// </summary>
    public static string? ValidateOpen(AgentTradeProposal p, RiskControlConfig config,
        OpenRiskContext context, out OpenTradePlan? plan)
    {
        plan = null;
        if (p.Action != ProposalAction.Open) return "仅支持开仓提案";
        if (p.Side is null) return "开仓提案缺少方向（long/short）";
        if (p.EntryPrice is not > 0) return "开仓提案缺少有效入场价";
        if (p.StopLossPrice is not { } stop) return "开仓提案缺少止损价";
        if (p.TakeProfitPrice is not { } take) return "开仓提案缺少止盈价";

        var isLong = p.Side == TradeSide.Long;
        var entry = p.EntryPrice.Value;
        if (isLong ? stop >= entry : stop <= entry) return "开仓提案止损方向错误";
        if (isLong ? take <= entry : take >= entry) return "开仓提案止盈方向错误";

        return TradingRiskPolicy.ValidateOpen(ToLegacyDecision(p), config, context, out plan);
    }

    /// <summary>严格转换：非法 action/side/复核清单 JSON 时返回错误，不抛异常。</summary>
    public static bool TryToContract(LuckyCat.Models.TradeProposal p, out AgentTradeProposal? contract,
        out string? error)
    {
        contract = null;
        if (!Enum.TryParse<ProposalAction>(p.Action, ignoreCase: true, out var action)
            || !Enum.IsDefined(action))
        {
            error = $"提案动作无效：{p.Action}";
            return false;
        }

        TradeSide? side = null;
        if (!string.IsNullOrWhiteSpace(p.Side))
        {
            if (!Enum.TryParse<TradeSide>(p.Side, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                error = $"提案方向无效：{p.Side}";
                return false;
            }
            side = parsed;
        }

        List<string>? checklist = null;
        if (p.ReviewChecklistJson is not null)
        {
            try
            {
                checklist = JsonSerializer.Deserialize<List<string>>(p.ReviewChecklistJson);
            }
            catch (JsonException)
            {
                error = "提案复核清单 JSON 无效";
                return false;
            }
        }

        contract = new AgentTradeProposal(action, p.Symbol, side, p.Confidence, p.EntryPrice,
            p.StopLossPrice, p.TakeProfitPrice, p.PositionPct, p.Leverage, p.Thesis, p.Invalidation,
            checklist);
        error = null;
        return true;
    }

    /// <summary>宽松转换：非法输入降级为 Hold 契约（不会进入开仓执行），严格校验请用 <see cref="TryToContract"/>。</summary>
    public static AgentTradeProposal ToContract(LuckyCat.Models.TradeProposal p) =>
        TryToContract(p, out var contract, out _)
            ? contract!
            : new AgentTradeProposal(ProposalAction.Hold, p.Symbol, null, p.Confidence, null, null, null,
                null, null, p.Thesis, p.Invalidation, null);

    public static JsonElement ToLegacyDecision(LuckyCat.Models.TradeProposal p) => ToLegacyDecision(ToContract(p));

    public static string? ValidateOpen(LuckyCat.Models.TradeProposal p, RiskControlConfig config,
        OpenRiskContext context, out OpenTradePlan? plan)
    {
        plan = null;
        return TryToContract(p, out var contract, out var error)
            ? ValidateOpen(contract!, config, context, out plan)
            : error;
    }
}
