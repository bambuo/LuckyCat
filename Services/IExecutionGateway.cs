using CryptoExchange.Net.SharedApis;

namespace LuckyCat.Services;

/// <summary>
/// 下单结果分类：
/// <list type="bullet">
/// <item><see cref="NotCreated"/>：交易所已明确拒绝/未创建订单（可用于有限次重试）；</item>
/// <item><see cref="Created"/>：交易所已受理/创建订单（含尚未回报成交）；</item>
/// <item><see cref="Unknown"/>：超时、网络/解析异常或无法确认是否创建——一律按未知处理，禁止重试。</item>
/// </list>
/// </summary>
public enum PlaceOutcome
{
    NotCreated,
    Created,
    Unknown
}

/// <summary>下单统一结果。只有 <see cref="PlaceOutcome.Created"/> 视为成功；<see cref="Error"/> 仅供内部日志，不得原样回显。</summary>
public sealed record PlaceResult(
    PlaceOutcome Outcome,
    string? ExchangeOrderId = null,
    decimal? FilledQuantity = null,
    decimal? AveragePrice = null,
    string? Error = null)
{
    public bool Ok => Outcome == PlaceOutcome.Created;

    public static PlaceResult Created(string? exchangeOrderId = null, decimal? filledQuantity = null,
        decimal? averagePrice = null) =>
        new(PlaceOutcome.Created, exchangeOrderId, filledQuantity, averagePrice);

    public static PlaceResult NotCreated(string? error = null) =>
        new(PlaceOutcome.NotCreated, Error: error);

    public static PlaceResult Unknown(string? error = null) =>
        new(PlaceOutcome.Unknown, Error: error);
}

/// <summary>
/// 执行网关：智能体写工具的唯一交易所出口（杠杆/仓位模式/市价或限价下单/整仓或部分平仓/撤单/行情与合约规格/能力审计）。
/// 下单返回带类型的结果（未创建/已创建/未知），失败不得以异常文本猜测结果；平仓可携带工具生成的 clientOrderId 与订单类型（market/limit）。
/// 测试与演练用假实现替换；真实实现见 <see cref="ExecutionGateway"/>。
/// </summary>
public interface IExecutionGateway
{
    Task<decimal?> GetPriceAsync(SharedSymbol symbol, CancellationToken ct);
    Task<SharedFuturesSymbol?> GetFuturesSpecAsync(SharedSymbol symbol, CancellationToken ct);
    Task<(bool Ok, string? Error)> SetLeverageAsync(SharedSymbol symbol, decimal leverage, CancellationToken ct);
    Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct);

    Task<PlaceResult> PlaceAsync(
        SharedSymbol symbol, bool isLong, decimal quantity, decimal stopLossPrice, decimal? takeProfitPrice,
        decimal leverage, string orderType, decimal? price, string? clientOrderId, CancellationToken ct);

    /// <summary>旧调用方兼容：市价单、无客户端订单号。</summary>
    Task<PlaceResult> PlaceAsync(
        SharedSymbol symbol, bool isLong, decimal quantity, decimal stopLossPrice, decimal takeProfitPrice,
        decimal leverage, CancellationToken ct) =>
        PlaceAsync(symbol, isLong, quantity, stopLossPrice, takeProfitPrice, leverage, "market", null, null, ct);

    /// <summary>
    /// 平仓（市价/限价、整仓/部分）。成功时返回实际提交数量、参考价与交易所订单号：
    /// 整仓原生平仓可能无订单号（null）；部分/限价反向减仓单返回交易所订单号，供 Task 8 撤单/审计。
    /// </summary>
    Task<(bool Ok, string? Error, decimal? Quantity, decimal? Price, string? ExchangeOrderId)> CloseAsync(
        SharedSymbol symbol, decimal? quantity, bool isLong, string orderType, decimal? price,
        string? clientOrderId, CancellationToken ct);

    /// <summary>旧调用方兼容：市价平仓、无客户端订单号、丢弃交易所订单号。</summary>
    async Task<(bool Ok, string? Error, decimal? Quantity, decimal? Price)> CloseAsync(
        SharedSymbol symbol, decimal? quantity, bool isLong, CancellationToken ct)
    {
        var result = await CloseAsync(symbol, quantity, isLong, "market", null, null, ct).ConfigureAwait(false);
        return (result.Ok, result.Error, result.Quantity, result.Price);
    }

    Task<(bool Ok, string? Error)> CancelAsync(SharedSymbol symbol, string orderId, CancellationToken ct);
    TradingAudit Audit();
}
