using CryptoExchange.Net.SharedApis;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>经过风控的订单提交端口，测试可替换成记录调用的适配器。</summary>
public interface IOpenOrderGateway
{
    Task<(bool Ok, string? Error)> SetLeverageAsync(decimal leverage, CancellationToken ct);
    Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct);
    Task<(bool Ok, string? Error)> PlaceAsync(OpenTradePlan plan, bool isLong, SharedPositionSide? side, CancellationToken ct);
}

public static class OpenOrderSubmission
{
    public static async Task<(bool Ok, string? Error)> SubmitAsync(IOpenOrderGateway gateway, OpenTradePlan plan, bool isLong, CancellationToken ct)
    {
        var leverage = await gateway.SetLeverageAsync(plan.Leverage, ct);
        if (!leverage.Ok) return (false, $"杠杆设置失败，未下单：{leverage.Error}");
        var mode = await gateway.GetPositionModeAsync(ct);
        if (mode is null) return (false, "持仓模式未知，未下单");
        var side = mode == SharedPositionMode.HedgeMode ? (isLong ? SharedPositionSide.Long : SharedPositionSide.Short) : (SharedPositionSide?)null;
        return await gateway.PlaceAsync(plan, isLong, side, ct);
    }
}

public sealed class ExchangeOpenOrderGateway(ExchangeAccount account, SharedSymbol symbol, bool crossMargin,
    ExchangeTradingService trading) : IOpenOrderGateway
{
    public Task<(bool Ok, string? Error)> SetLeverageAsync(decimal leverage, CancellationToken ct) =>
        ExchangeTradingService.SetLeverageAsync(account, symbol, leverage, crossMargin, ct);
    public Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct) => trading.GetPositionModeAsync(account, ct);
    public Task<(bool Ok, string? Error)> PlaceAsync(OpenTradePlan plan, bool isLong, SharedPositionSide? side, CancellationToken ct) =>
        ExchangeTradingService.PlaceMarketAsync(account, symbol, isLong, plan.Quantity, plan.StopLossPrice, plan.TakeProfitPrice, crossMargin, side, ct);
}
