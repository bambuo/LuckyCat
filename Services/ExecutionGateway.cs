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

    public async Task<PlaceResult> PlaceAsync(
        SharedSymbol symbol, bool isLong, decimal quantity, decimal stopLossPrice, decimal? takeProfitPrice,
        decimal leverage, string orderType, decimal? price, string? clientOrderId, CancellationToken ct)
    {
        if (quantity <= 0) return PlaceResult.NotCreated("下单数量必须大于 0");
        if (stopLossPrice <= 0) return PlaceResult.NotCreated("止损价必须大于 0");
        if (takeProfitPrice is < 0) return PlaceResult.NotCreated("止盈价不能为负数");
        if (!ExchangeTradingService.TryParseOrderType(orderType, out var parsedType))
            return PlaceResult.NotCreated($"不支持的订单类型：{orderType}");
        if (parsedType == SharedOrderType.Limit && price is not > 0)
            return PlaceResult.NotCreated("限价单缺少有效价格");

        // 与 OpenOrderSubmission 相同的硬顺序：杠杆设置失败不下单；持仓模式未知不下单（无法安全表达方向）。
        // 这两步都发生在提交订单之前，因此结果确定是「未创建」。
        var (leverageOk, leverageError) = await SetLeverageAsync(symbol, leverage, ct).ConfigureAwait(false);
        if (!leverageOk) return PlaceResult.NotCreated($"杠杆设置失败，未下单：{leverageError}");
        var mode = await GetPositionModeAsync(ct).ConfigureAwait(false);
        if (mode is null) return PlaceResult.NotCreated("持仓模式未知，未下单");
        var side = mode == SharedPositionMode.HedgeMode
            ? (isLong ? SharedPositionSide.Long : SharedPositionSide.Short)
            : (SharedPositionSide?)null;

        // 止损是开仓硬性保护：<= 0 直接拒绝，绝不静默丢弃；止盈 0 视为未提供（本轮策略允许无固定止盈）。
        // 结果分类由交易服务给出：交易所明确拒绝=NotCreated，超时/异常/无法判定=Unknown。
        var placed = await ExchangeTradingService.PlaceOrderAsync(
            account, symbol, isLong, quantity, parsedType,
            parsedType == SharedOrderType.Limit ? price : null,
            stopLossPrice,
            takeProfitPrice is > 0 ? takeProfitPrice : null,
            crossMargin, side, clientOrderId, ct).ConfigureAwait(false);

        // 成交量/均价由成交回报异步确认，此处如实返回 null（不伪造成交）；订单号必须透传。
        return placed;
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
