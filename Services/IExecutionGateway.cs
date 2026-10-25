using CryptoExchange.Net.SharedApis;

namespace LuckyCat.Services;

/// <summary>
/// 执行网关：智能体写工具的唯一交易所出口（杠杆/仓位模式/市价开仓/平仓/撤单/行情与合约规格/能力审计）。
/// 测试与演练用假实现替换；真实实现见 <see cref="ExecutionGateway"/>。
/// </summary>
public interface IExecutionGateway
{
    Task<decimal?> GetPriceAsync(SharedSymbol symbol, CancellationToken ct);
    Task<SharedFuturesSymbol?> GetFuturesSpecAsync(SharedSymbol symbol, CancellationToken ct);
    Task<(bool Ok, string? Error)> SetLeverageAsync(SharedSymbol symbol, decimal leverage, CancellationToken ct);
    Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct);
    Task<(bool Ok, string? Error, string? ExchangeOrderId, decimal? FilledQuantity, decimal? AveragePrice)> PlaceAsync(
        SharedSymbol symbol, bool isLong, decimal quantity, decimal stopLossPrice, decimal takeProfitPrice,
        decimal leverage, CancellationToken ct);
    Task<(bool Ok, string? Error, decimal? Quantity, decimal? Price)> CloseAsync(
        SharedSymbol symbol, decimal? quantity, bool isLong, CancellationToken ct);
    Task<(bool Ok, string? Error)> CancelAsync(SharedSymbol symbol, string orderId, CancellationToken ct);
    TradingAudit Audit();
}
