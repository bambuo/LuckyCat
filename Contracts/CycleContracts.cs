namespace LuckyCat.Contracts;

// API DTO：金额、数量、百分比、杠杆一律字符串传输（LC-STD-03）。
public sealed class TradeProposalDto
{
    public required string Id { get; set; }
    public required string Action { get; set; }
    public string? Symbol { get; set; }
    public string? Side { get; set; }
    public string? Confidence { get; set; }
    public string? EntryPrice { get; set; }
    public string? StopLossPrice { get; set; }
    public string? TakeProfitPrice { get; set; }
    public string? PositionPct { get; set; }
    public string? Leverage { get; set; }
    public required string Thesis { get; set; }
    public string? Invalidation { get; set; }
}

// 手动平仓请求（Task 14 端点使用，Task 11 CloseManuallyAsync 消费）。
public sealed record ManualCloseRequest(string Symbol, decimal? Quantity, string OrderType, decimal? Price, string Reason);
