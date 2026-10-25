using CryptoExchange.Net.SharedApis;
using LuckyCat.Models;
using LuckyCat.Services;
using Xunit;

namespace LuckyCat.Tests;

/// <summary>执行网关可离线验证的映射：订单类型解析与下单请求构造（不触网）。</summary>
public class ExecutionGatewayTests
{
    private static readonly SharedSymbol Btc = new(TradingMode.PerpetualLinear, "BTC", "USDT");

    private static ExchangeAccount Account() => new()
    {
        Id = 1, ExchangeType = ExchangeType.Binance, AccountName = "test",
        ApiKey = "key", ApiSecret = "secret"
    };

    [Theory]
    [InlineData("market", SharedOrderType.Market, true)]
    [InlineData("MARKET", SharedOrderType.Market, true)]
    [InlineData("limit", SharedOrderType.Limit, true)]
    [InlineData("Limit", SharedOrderType.Limit, true)]
    [InlineData(null, SharedOrderType.Market, true)]
    [InlineData("stop", SharedOrderType.Market, false)]
    public void Tool_order_type_is_parsed_strictly(string? value, SharedOrderType expected, bool ok)
    {
        Assert.Equal(ok, ExchangeTradingService.TryParseOrderType(value, out var parsed));
        if (ok) Assert.Equal(expected, parsed);
    }

    [Fact]
    public void Limit_order_carries_price_tif_position_side_and_client_order_id()
    {
        var request = ExchangeTradingService.BuildFuturesOrderRequest(
            Account(), Btc, isLong: true, quantity: 0.5m,
            orderType: SharedOrderType.Limit, price: 101.5m, stopLossPrice: 99m, takeProfitPrice: 110m,
            crossMargin: true, positionSide: SharedPositionSide.Long, clientOrderId: "lc-open-1");

        Assert.Equal(SharedOrderType.Limit, request.OrderType);
        Assert.Equal(SharedOrderSide.Buy, request.Side);
        Assert.Equal(101.5m, request.Price);
        Assert.Equal(SharedTimeInForce.GoodTillCanceled, request.TimeInForce);
        Assert.Equal("lc-open-1", request.ClientOrderId);
        Assert.Equal(SharedPositionSide.Long, request.PositionSide);
        Assert.Equal(99m, request.StopLossPrice);
        Assert.Equal(110m, request.TakeProfitPrice);
        Assert.NotNull(request.Quantity);
    }

    [Fact]
    public void Market_order_keeps_client_order_id_but_has_no_price_or_tif()
    {
        var request = ExchangeTradingService.BuildFuturesOrderRequest(
            Account(), Btc, isLong: false, quantity: 0.25m,
            orderType: SharedOrderType.Market, price: 999m, stopLossPrice: null, takeProfitPrice: null,
            crossMargin: false, positionSide: null, clientOrderId: "lc-close-1", reduceOnly: true);

        Assert.Equal(SharedOrderType.Market, request.OrderType);
        Assert.Equal(SharedOrderSide.Sell, request.Side);
        Assert.Null(request.Price);
        Assert.Null(request.TimeInForce);
        Assert.Equal("lc-close-1", request.ClientOrderId);
        Assert.Null(request.PositionSide);
        Assert.True(request.ReduceOnly);
        Assert.Equal(SharedMarginMode.Isolated, request.MarginMode);
    }

    [Fact]
    public void Reduce_only_limit_close_uses_reverse_side_and_position_side_or_reduce_only()
    {
        var hedge = ExchangeTradingService.BuildReduceOnlyOrderRequest(
            Account(), Btc, isLong: true, quantity: 0.4m,
            orderType: SharedOrderType.Limit, price: 102m, crossMargin: true, hedgeStyle: true,
            clientOrderId: "lc-close-2");
        Assert.Equal(SharedOrderSide.Sell, hedge.Side);
        Assert.Equal(SharedOrderType.Limit, hedge.OrderType);
        Assert.Equal(102m, hedge.Price);
        Assert.Equal(SharedTimeInForce.GoodTillCanceled, hedge.TimeInForce);
        Assert.Equal(SharedPositionSide.Long, hedge.PositionSide);
        Assert.Equal("lc-close-2", hedge.ClientOrderId);
        Assert.Null(hedge.ReduceOnly);

        var oneWay = ExchangeTradingService.BuildReduceOnlyOrderRequest(
            Account(), Btc, isLong: true, quantity: 0.4m,
            orderType: SharedOrderType.Market, price: null, crossMargin: false, hedgeStyle: false);
        Assert.Equal(SharedOrderSide.Sell, oneWay.Side);
        Assert.Null(oneWay.Price);
        Assert.Null(oneWay.PositionSide);
        Assert.True(oneWay.ReduceOnly);
        Assert.Equal(SharedMarginMode.Isolated, oneWay.MarginMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Close_rejects_non_positive_quantity_before_touching_exchange(decimal quantity)
    {
        var (ok, error, submitted, price) = await new ExchangeTradingService()
            .ClosePositionAsync(Account(), Btc, quantity, "market", null, true, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("平仓数量必须大于 0", error);
        Assert.Null(submitted);
        Assert.Null(price);
    }

    [Fact]
    public async Task Close_rejects_limit_without_price_before_touching_exchange()
    {
        var (ok, error, _, _) = await new ExchangeTradingService()
            .ClosePositionAsync(Account(), Btc, 0.5m, "limit", null, true, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("限价平仓缺少有效价格", error);
    }

    [Fact]
    public async Task Close_rejects_unknown_order_type_before_touching_exchange()
    {
        var (ok, error, _, _) = await new ExchangeTradingService()
            .ClosePositionAsync(Account(), Btc, 0.5m, "stop", 100m, true, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("不支持的订单类型：stop", error);
    }

    [Fact]
    public async Task Place_rejects_non_positive_quantity_before_touching_exchange()
    {
        var result = await ExchangeTradingService.PlaceOrderAsync(
            Account(), Btc, isLong: true, quantity: 0m, orderType: SharedOrderType.Market, price: null,
            stopLossPrice: 95m, takeProfitPrice: 110m, crossMargin: true, positionSide: null,
            clientOrderId: "lc-open-1", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PlaceOutcome.NotCreated, result.Outcome);
        Assert.Equal("下单数量必须大于 0", result.Error);
        Assert.Null(result.ExchangeOrderId);
    }

    [Fact]
    public async Task Place_rejects_limit_without_price_before_touching_exchange()
    {
        var result = await ExchangeTradingService.PlaceOrderAsync(
            Account(), Btc, isLong: true, quantity: 0.1m, orderType: SharedOrderType.Limit, price: null,
            stopLossPrice: 95m, takeProfitPrice: 110m, crossMargin: true, positionSide: null,
            clientOrderId: "lc-open-1", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PlaceOutcome.NotCreated, result.Outcome);
        Assert.Equal("限价单缺少有效价格", result.Error);
        Assert.Null(result.ExchangeOrderId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Gateway_place_rejects_non_positive_stop_loss_before_touching_exchange(decimal stopLoss)
    {
        var gateway = new ExecutionGateway(Account(), new ExchangeTradingService());

        var result = await gateway.PlaceAsync(
            Btc, isLong: true, quantity: 0.1m, stopLossPrice: stopLoss, takeProfitPrice: 110m,
            leverage: 3m, orderType: "market", price: null, clientOrderId: "lc-open-1", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PlaceOutcome.NotCreated, result.Outcome);
        Assert.Equal("止损价必须大于 0", result.Error);
        Assert.Null(result.ExchangeOrderId);
        Assert.Null(result.FilledQuantity);
        Assert.Null(result.AveragePrice);
    }

    [Fact]
    public async Task Gateway_place_rejects_negative_take_profit_before_touching_exchange()
    {
        var gateway = new ExecutionGateway(Account(), new ExchangeTradingService());

        var result = await gateway.PlaceAsync(
            Btc, isLong: true, quantity: 0.1m, stopLossPrice: 95m, takeProfitPrice: -1m,
            leverage: 3m, orderType: "market", price: null, clientOrderId: "lc-open-1", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PlaceOutcome.NotCreated, result.Outcome);
        Assert.Equal("止盈价不能为负数", result.Error);
    }

    [Fact]
    public void Timeout_and_network_errors_are_ambiguous_but_exchange_rejections_are_not()
    {
        Assert.True(ExchangeTradingService.IsAmbiguousPlaceError(
            new CryptoExchange.Net.Objects.TimeoutError("timeout", new TimeoutException())));
        Assert.True(ExchangeTradingService.IsAmbiguousPlaceError(
            new CryptoExchange.Net.Objects.CantConnectError()));
        Assert.False(ExchangeTradingService.IsAmbiguousPlaceError(
            new CryptoExchange.Net.Objects.ServerError(
                CryptoExchange.Net.Objects.Errors.ErrorType.InsufficientBalance, "insufficient", new Exception())));
    }

    [Fact]
    public async Task Gateway_close_returns_exchange_order_id_slot()
    {
        var gateway = new ExecutionGateway(Account(), new ExchangeTradingService());

        var (ok, error, quantity, price, exchangeOrderId) = await gateway.CloseAsync(
            Btc, 0.5m, isLong: true, "stop", null, "lc-close-1", CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("不支持的订单类型：stop", error);
        Assert.Null(quantity);
        Assert.Null(price);
        Assert.Null(exchangeOrderId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Detailed_close_returns_exchange_order_id_slot(decimal quantity)
    {
        var (ok, error, submitted, price, exchangeOrderId) = await new ExchangeTradingService()
            .ClosePositionAsync(Account(), Btc, quantity, "market", null, "lc-close-1", true, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("平仓数量必须大于 0", error);
        Assert.Null(submitted);
        Assert.Null(price);
        Assert.Null(exchangeOrderId);
    }
}
