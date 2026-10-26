namespace LuckyCat.Tools;

/// <summary>工具错误码（稳定契约，前端与提示词按此分支；新增只能追加，不改既有语义）。</summary>
public static class ToolErrors
{
    public const string SymbolNotAllowed = "symbol_not_allowed";
    public const string PositionNotFound = "position_not_found";
    public const string InvalidArgument = "invalid_argument";
    public const string DataUnavailable = "data_unavailable";
    public const string StaleMarketData = "stale_market_data";
    public const string ProposalNotFound = "proposal_not_found";
    public const string ProposalNotApproved = "proposal_not_approved";
    public const string ProposalExpired = "proposal_expired";
    public const string QuantityExceeded = "quantity_exceeded";
    public const string LeverageExceeded = "leverage_exceeded";
    public const string PriceOutOfBand = "price_out_of_band";
    public const string RiskPolicyViolation = "risk_policy_violation";
    public const string DuplicateOrder = "duplicate_order";
    public const string OrderLimitReached = "order_limit_reached";
    public const string OrderNotCancellable = "order_not_cancellable";
    public const string ToolBudgetExceeded = "tool_budget_exceeded";
    public const string ExchangeError = "exchange_error";
    public const string ExchangeUnknown = "exchange_unknown";
    public const string ToolInternalError = "tool_internal_error";
}
