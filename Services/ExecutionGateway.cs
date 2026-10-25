using CryptoExchange.Net.SharedApis;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>
/// 实盘执行网关：包装 <see cref="ExchangeTradingService"/> 与账户，作为智能体写工具的唯一交易所出口。
/// 开仓顺序与 <see cref="OpenOrderSubmission"/> 一致：先设杠杆（失败即不下单）→ 再取持仓模式（未知不下单）→ 下单（市价/限价）；
/// 平仓支持整仓与部分数量（反向减仓单）；撤单走共享 API。
/// 所有方法如实返回错误与交易所订单号，不吞异常、不伪造成交（成交量/均价待成交回报确认）。
/// </summary>
public sealed class ExecutionGateway(ExchangeAccount account, ExchangeTradingService trading, bool crossMargin = true)
    : IExecutionGateway
{
    public Task<decimal?> GetPriceAsync(SharedSymbol symbol, CancellationToken ct) =>
        ExchangeTradingService.GetPriceAsync(account, symbol, ct);

    public Task<SharedFuturesSymbol?> GetFuturesSpecAsync(SharedSymbol symbol, CancellationToken ct) =>
        ExchangeTradingService.GetFuturesSymbolAsync(account, symbol, ct);

    public Task<(bool Ok, string? Error)> SetLeverageAsync(SharedSymbol symbol, decimal leverage, CancellationToken ct) =>
        ExchangeTradingService.SetLeverageAsync(account, symbol, leverage, crossMargin, ct);

    public Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct) =>
        trading.GetPositionModeAsync(account, ct);

    public async Task<(bool Ok, string? Error, string? ExchangeOrderId, decimal? FilledQuantity, decimal? AveragePrice)> PlaceAsync(
        SharedSymbol symbol, bool isLong, decimal quantity, decimal stopLossPrice, decimal? takeProfitPrice,
        decimal leverage, string orderType, decimal? price, string? clientOrderId, CancellationToken ct)
    {
        if (quantity <= 0) return (false, "下单数量必须大于 0", null, null, null);
        if (stopLossPrice <= 0) return (false, "止损价必须大于 0", null, null, null);
        if (takeProfitPrice is < 0) return (false, "止盈价不能为负数", null, null, null);
        if (!ExchangeTradingService.TryParseOrderType(orderType, out var parsedType))
            return (false, $"不支持的订单类型：{orderType}", null, null, null);
        if (parsedType == SharedOrderType.Limit && price is not > 0)
            return (false, "限价单缺少有效价格", null, null, null);

        // 与 OpenOrderSubmission 相同的硬顺序：杠杆设置失败不下单；持仓模式未知不下单（无法安全表达方向）。
        var (leverageOk, leverageError) = await SetLeverageAsync(symbol, leverage, ct).ConfigureAwait(false);
        if (!leverageOk) return (false, $"杠杆设置失败，未下单：{leverageError}", null, null, null);
        var mode = await GetPositionModeAsync(ct).ConfigureAwait(false);
        if (mode is null) return (false, "持仓模式未知，未下单", null, null, null);
        var side = mode == SharedPositionMode.HedgeMode
            ? (isLong ? SharedPositionSide.Long : SharedPositionSide.Short)
            : (SharedPositionSide?)null;

        // 止损是开仓硬性保护：<= 0 直接拒绝，绝不静默丢弃；止盈 0 视为未提供（本轮策略允许无固定止盈）。
        var (ok, error, orderId) = await ExchangeTradingService.PlaceOrderAsync(
            account, symbol, isLong, quantity, parsedType,
            parsedType == SharedOrderType.Limit ? price : null,
            stopLossPrice,
            takeProfitPrice is > 0 ? takeProfitPrice : null,
            crossMargin, side, clientOrderId, ct).ConfigureAwait(false);

        // 成交量/均价由成交回报异步确认，此处如实返回 null（不伪造成交）；订单号必须透传。
        return (ok, error, orderId, null, null);
    }

    public async Task<(bool Ok, string? Error, decimal? Quantity, decimal? Price, string? ExchangeOrderId)> CloseAsync(
        SharedSymbol symbol, decimal? quantity, bool isLong, string orderType, decimal? price,
        string? clientOrderId, CancellationToken ct)
    {
        if (!ExchangeTradingService.TryParseOrderType(orderType, out _))
            return (false, $"不支持的订单类型：{orderType}", null, null, null);

        _ = isLong; // 方向由交易所最新持仓判定；调用方传入值仅作审计参考。
        return await trading.ClosePositionAsync(account, symbol, quantity, orderType, price, clientOrderId,
            crossMargin, ct).ConfigureAwait(false);
    }

    public Task<(bool Ok, string? Error)> CancelAsync(SharedSymbol symbol, string orderId, CancellationToken ct) =>
        ExchangeTradingService.CancelOrderAsync(account, symbol, orderId, ct);

    public TradingAudit Audit() => ExchangeTradingService.Audit(account);
}
