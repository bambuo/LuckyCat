using CryptoExchange.Net.SharedApis;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>
/// 实盘执行网关：包装 <see cref="ExchangeTradingService"/> 与账户，作为智能体写工具的唯一交易所出口。
/// 开仓顺序与 <see cref="OpenOrderSubmission"/> 一致：先设杠杆（失败即不下单）→ 再取持仓模式（未知不下单）→ 市价下单；
/// 平仓复用 <see cref="ExchangeTradingService.ClosePositionAsync"/> 的整仓语义；撤单走共享 API。
/// 所有方法如实返回错误，不吞异常、不伪造成交。
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
        SharedSymbol symbol, bool isLong, decimal quantity, decimal stopLossPrice, decimal takeProfitPrice,
        decimal leverage, CancellationToken ct)
    {
        // 与 OpenOrderSubmission 相同的硬顺序：杠杆设置失败不下单；持仓模式未知不下单（无法安全表达方向）。
        var (leverageOk, leverageError) = await SetLeverageAsync(symbol, leverage, ct).ConfigureAwait(false);
        if (!leverageOk) return (false, $"杠杆设置失败，未下单：{leverageError}", null, null, null);
        var mode = await GetPositionModeAsync(ct).ConfigureAwait(false);
        if (mode is null) return (false, "持仓模式未知，未下单", null, null, null);
        var side = mode == SharedPositionMode.HedgeMode
            ? (isLong ? SharedPositionSide.Long : SharedPositionSide.Short)
            : (SharedPositionSide?)null;
        var (ok, error) = await ExchangeTradingService.PlaceMarketAsync(
            account, symbol, isLong, quantity, stopLossPrice, takeProfitPrice, crossMargin, side, ct).ConfigureAwait(false);
        // 现有 PlaceMarketAsync 只返回成功/错误，拿不到交易所订单号与成交明细；此处如实返回 null（不伪造成交）。
        return (ok, error, null, null, null);
    }

    public async Task<(bool Ok, string? Error, decimal? Quantity, decimal? Price)> CloseAsync(
        SharedSymbol symbol, decimal? quantity, bool isLong, CancellationToken ct)
    {
        // 现有链路只有整仓平仓（ICloseFullPosition + 反向市价兜底）：数量小于当前持仓时明确拒绝，
        // 避免「部分平仓」被静默放大成整仓；数量为空或等于整仓时按整仓执行。
        if (quantity is { } requested)
        {
            if (requested <= 0) return (false, "平仓数量必须大于 0", null, null);
            var positions = await trading.GetPositionSnapshotAsync(account, ct).ConfigureAwait(false);
            if (!positions.Success) return (false, $"持仓不可读：{positions.Error}", null, null);
            var nativeName = (await ExchangeTradingService.GetFuturesSymbolAsync(account, symbol, ct).ConfigureAwait(false))?.Name;
            var position = positions.Positions.FirstOrDefault(p =>
                (nativeName is not null && string.Equals(p.Symbol, nativeName, StringComparison.OrdinalIgnoreCase))
                || p.Symbol.Contains(symbol.BaseAsset, StringComparison.OrdinalIgnoreCase));
            if (position is null) return (false, "未找到该标的持仓", null, null);
            if (requested < Math.Abs(position.Quantity)) return (false, "当前仅支持整仓平仓，平仓数量小于持仓", null, null);
        }

        _ = isLong; // 方向由交易所最新持仓判定；调用方传入值仅作审计参考。
        return await trading.ClosePositionAsync(account, symbol, crossMargin, ct).ConfigureAwait(false);
    }

    public Task<(bool Ok, string? Error)> CancelAsync(SharedSymbol symbol, string orderId, CancellationToken ct) =>
        ExchangeTradingService.CancelOrderAsync(account, symbol, orderId, ct);

    public TradingAudit Audit() => ExchangeTradingService.Audit(account);
}
