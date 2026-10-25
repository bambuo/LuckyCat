namespace LuckyCat.Tools;

/// <summary>
/// 工具清单的稳定版本与各角色可用工具名（Task 9 的提示词指纹、Task 10 的注册共用；
/// 修改清单必须同步提升 Version，避免提示词缓存与工具集合漂移）。
/// </summary>
public static class ToolCatalog
{
    public const string Version = "tools-v1";
    public static readonly string[] Analyst = ["get_coin_candidates", "get_market_overview", "get_klines", "get_indicators", "get_sentiment", "get_news"];
    public static readonly string[] Trader = ["get_klines", "get_indicators", "get_sentiment", "get_account", "get_positions", "get_recent_orders", "get_closed_trades", "get_decision_memory", "get_lessons", "get_trading_discipline"];
    public static readonly string[] Risk = ["get_account", "get_positions", "get_risk_policy", "get_trading_discipline"];
    public static readonly string[] Executor = ["get_account", "get_positions", "get_recent_orders", "place_order", "close_position", "cancel_order"];
    public static readonly string[] Reviewer = ["get_closed_trades", "get_cycles", "get_lessons"];
    public static bool IsWrite(string tool) => tool is "place_order" or "close_position" or "cancel_order";
}
