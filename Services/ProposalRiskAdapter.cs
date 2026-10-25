using System.Text.Json;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// 智能体提案 → 旧版风控契约的适配层：
/// ① 动作：open 按方向映射 buy/sell，close/hold 保持原样（<see cref="ValidateOpen"/> 直接拒绝非开仓）；
/// ② 信心度：提案为 0–1，旧契约要求 0–100 整数，此处四舍五入放大 100 倍；
/// ③ 止损/止盈：提案为绝对价格，旧契约要求相对入场价的百分点，此处按 |入场价-保护价|/入场价 换算。
/// 实体重载（<see cref="LuckyCat.Models.TradeProposal"/>）供写工具直接复用，避免调用方各写一遍换算。
/// </summary>
public static class ProposalRiskAdapter
{
    public static JsonElement ToLegacyDecision(AgentTradeProposal p)
    {
        var isLong = p.Side == TradeSide.Long;
        var action = p.Action switch
        {
            ProposalAction.Open => isLong ? "buy" : "sell",
            ProposalAction.Close => "close",
            _ => "hold"
        };
        decimal? stopPct = p.EntryPrice is > 0 && p.StopLossPrice is { } sl
            ? Math.Abs(p.EntryPrice.Value - sl) / p.EntryPrice.Value * 100m : null;
        decimal? takePct = p.EntryPrice is > 0 && p.TakeProfitPrice is { } tp
            ? Math.Abs(tp - p.EntryPrice.Value) / p.EntryPrice.Value * 100m : null;
        var obj = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["symbol"] = p.Symbol,
            ["confidence"] = p.Confidence is { } c ? Math.Round(c * 100m, 0) : null,
            ["reason"] = p.Thesis,
            ["entry_price"] = p.EntryPrice,
            ["stop_loss_pct"] = stopPct,
            ["take_profit_pct"] = takePct,
            ["position_pct"] = p.PositionPct,
            ["leverage"] = p.Leverage,
            ["trigger"] = p.Action == ProposalAction.Hold ? "hold" : null
        };
        return JsonSerializer.SerializeToElement(obj);
    }

    /// <summary>仅开仓提案进入旧版风控；close/hold 直接拒绝（不会伪造可执行计划）。</summary>
    public static string? ValidateOpen(AgentTradeProposal p, RiskControlConfig config,
        OpenRiskContext context, out OpenTradePlan? plan)
    {
        if (p.Action != ProposalAction.Open) { plan = null; return "仅支持开仓提案"; }
        return TradingRiskPolicy.ValidateOpen(ToLegacyDecision(p), config, context, out plan);
    }

    public static AgentTradeProposal ToContract(LuckyCat.Models.TradeProposal p) => new(
        Enum.Parse<ProposalAction>(p.Action, ignoreCase: true),
        p.Symbol,
        p.Side is null ? null : Enum.Parse<TradeSide>(p.Side, ignoreCase: true),
        p.Confidence, p.EntryPrice, p.StopLossPrice, p.TakeProfitPrice, p.PositionPct, p.Leverage,
        p.Thesis, p.Invalidation,
        p.ReviewChecklistJson is null ? null : JsonSerializer.Deserialize<List<string>>(p.ReviewChecklistJson));

    public static JsonElement ToLegacyDecision(LuckyCat.Models.TradeProposal p) => ToLegacyDecision(ToContract(p));

    public static string? ValidateOpen(LuckyCat.Models.TradeProposal p, RiskControlConfig config,
        OpenRiskContext context, out OpenTradePlan? plan) => ValidateOpen(ToContract(p), config, context, out plan);
}
