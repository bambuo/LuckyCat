using System.Security.Cryptography;
using System.Text;
using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LuckyCat.Tests;

public class ExecutionToolsTests
{
    private const int TraderId = 7;
    private const int StrategyId = 3;
    private const int AccountId = 1;

    [Fact]
    public async Task Unapproved_proposal_is_rejected()
    {
        await using var env = await Env.CreateAsync();

        var result = await env.Tools.PlaceOrderAsync(env.UnapprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ProposalNotApproved, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Expired_proposal_is_rejected()
    {
        await using var env = await Env.CreateAsync();
        env.Verdict.ValidUntilUtc = DateTime.UtcNow.AddMinutes(-1);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ProposalExpired, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
    }

    [Theory]
    [InlineData("ETH/USDT", "long", 95, 110)]
    [InlineData("BTC/USDT", "short", 95, 110)]
    [InlineData("BTC/USDT", "long", 94, 110)]
    [InlineData("BTC/USDT", "long", 95, 111)]
    public async Task Proposal_parameter_mismatch_is_rejected(string symbol, string side,
        decimal stopLoss, decimal takeProfit)
    {
        await using var env = await Env.CreateAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, symbol, side, "limit",
            0.4m, 100m, 4m, stopLoss, takeProfit, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
    }

    [Fact]
    public async Task Take_profit_is_required()
    {
        await using var env = await Env.CreateAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, null, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
    }

    [Fact]
    public async Task Margin_uses_executor_leverage_and_current_equity()
    {
        await using var env = await Env.CreateAsync(equity: 10_000m, approvedPct: 1m, approvedLeverage: 5m);

        var good = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default); // 0.4*100/4 = 10 <= 1%*10000 = 100
        Assert.True(good.Ok, good.Error?.Message);

        var bad = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            13m, 100m, 4m, 95m, 110m, "test", default); // 13*100/4 = 325 > 100
        Assert.False(bad.Ok);
        Assert.Equal(ToolErrors.QuantityExceeded, bad.Error!.Code);
        Assert.Single(await env.Db.TraderOrders.ToListAsync());
    }

    [Fact]
    public async Task Unknown_margin_is_rejected_fail_closed()
    {
        SymbolPosition[] positions =
        [
            new("ETH/USDT", SharedPositionSide.Long, 1m, 3_000m, null, null, 5m)
        ];
        await using var env = await Env.CreateAsync(positions: positions);

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.DataUnavailable, result.Error!.Code);
        Assert.Contains("保证金无法确认", result.Error.Message);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Leverage_above_approval_is_rejected()
    {
        await using var env = await Env.CreateAsync(approvedLeverage: 5m);

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 6m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.LeverageExceeded, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
    }

    [Fact]
    public async Task Wider_position_verdict_is_rejected()
    {
        await using var env = await Env.CreateAsync(approvedPct: 3m); // 提案 1%，裁决 3% 属于放宽

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.RiskPolicyViolation, result.Error!.Code);
        Assert.Contains("仓位上限超过提案", result.Error.Message);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Wider_leverage_verdict_is_rejected()
    {
        await using var env = await Env.CreateAsync(approvedLeverage: 6m); // 提案 5 倍，裁决 6 倍属于放宽

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.LeverageExceeded, result.Error!.Code);
        Assert.Contains("杠杆上限超过提案", result.Error.Message);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
    }

    [Fact]
    public async Task Quantity_above_approved_plan_is_rejected()
    {
        await using var env = await Env.CreateAsync();

        // 1% × 5 倍 = 名义 500，计划数量 5；5.001 已超过批准保证金/重跑计划。
        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            5.001m, 100m, 5m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.QuantityExceeded, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Limit_price_outside_approved_zone_is_rejected()
    {
        await using var env = await Env.CreateAsync(entryZoneMin: 98m, entryZoneMax: 102m);

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 105m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.PriceOutOfBand, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
    }

    [Theory]
    [InlineData("long", 95, 110, 95)]
    [InlineData("long", 95, 110, 110)]
    [InlineData("short", 105, 90, 105)]
    [InlineData("short", 105, 90, 90)]
    public async Task Execution_price_must_be_strictly_between_stop_and_take(string side,
        decimal stopLoss, decimal takeProfit, decimal limitPrice)
    {
        await using var env = await Env.CreateAsync(entryZoneMin: 85m, entryZoneMax: 115m);
        env.Proposal.Side = side;
        env.Proposal.StopLossPrice = stopLoss;
        env.Proposal.TakeProfitPrice = takeProfit;
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", side, "limit",
            0.4m, limitPrice, 4m, stopLoss, takeProfit, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
        Assert.Contains("止损与止盈之间", result.Error.Message);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Market_execution_price_must_be_between_stop_and_take()
    {
        await using var env = await Env.CreateAsync();
        env.Gateway.Price = 95m; // 等于止损价

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "market",
            0.4m, null, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
        Assert.Contains("止损与止盈之间", result.Error.Message);
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Stale_market_data_is_rejected()
    {
        await using var env = await Env.CreateAsync(marketAsOf: DateTime.UtcNow.AddMinutes(-4));

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.StaleMarketData, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
    }

    [Fact]
    public async Task Future_market_timestamp_is_rejected()
    {
        await using var env = await Env.CreateAsync(marketAsOf: DateTime.UtcNow.AddMinutes(1));

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.StaleMarketData, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Stale_market_and_unknown_spec_returns_stale_market_data()
    {
        await using var env = await Env.CreateAsync(marketAsOf: DateTime.UtcNow.AddMinutes(-4));
        env.Gateway.Spec = null;

        // §6 顺序：第 8 步行情新鲜度必须先于第 10 步交易所规格返回。
        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.StaleMarketData, result.Error!.Code);
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Unknown_spec_with_fresh_market_is_rejected()
    {
        await using var env = await Env.CreateAsync();
        env.Gateway.Spec = null;

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("min")]
    [InlineData("notional")]
    [InlineData("quantityStep")]
    [InlineData("priceStep")]
    public async Task Unknown_exchange_constraint_is_rejected(string field)
    {
        await using var env = await Env.CreateAsync();
        env.Gateway.Spec = field switch
        {
            "max" => BuildSpec(maxTradeQuantity: null),
            "min" => BuildSpec(minTradeQuantity: null),
            "notional" => BuildSpec(minNotionalValue: null),
            "quantityStep" => BuildSpec(quantityStep: null),
            "priceStep" => BuildSpec(priceStep: null),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Duplicate_client_order_id_is_rejected()
    {
        await using var env = await Env.CreateAsync();
        env.Db.TraderOrders.Add(new TraderOrder
        {
            TraderId = TraderId,
            At = DateTime.UtcNow,
            Symbol = "BTC/USDT",
            Action = "open",
            Side = "long",
            Quantity = 0.4m,
            Price = 100m,
            CycleId = env.CycleId,
            ExchangeAccountId = AccountId,
            ProposalId = env.UnapprovedId,
            VerdictId = env.VerdictId,
            ClientOrderId = ClientOrderId(env.CycleId, env.ApprovedId),
            Status = "failed",
            Success = false
        });
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.DuplicateOrder, result.Error!.Code);
        Assert.Single(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Legacy_buy_row_blocks_recent_duplicate()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Db.TraderOrders.Add(new TraderOrder
        {
            TraderId = TraderId,
            At = DateTime.UtcNow.AddSeconds(-1),
            Symbol = "BTC/USDT",
            Action = "buy", // 过渡期旧行
            Side = "long",
            Quantity = 0.4m,
            Price = 100m,
            CycleId = Ids.NewUlid(),
            ExchangeAccountId = AccountId,
            Status = "filled",
            Success = true
        });
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderLimitReached, result.Error!.Code);
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Simulated_rows_do_not_block_live_ten_second_check()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Db.TraderOrders.Add(new TraderOrder
        {
            TraderId = TraderId,
            At = DateTime.UtcNow,
            Symbol = "BTC/USDT",
            Action = "open",
            Side = "long",
            Quantity = 0.4m,
            Price = 100m,
            CycleId = Ids.NewUlid(),
            ExchangeAccountId = AccountId,
            Status = "filled",
            Simulated = true,
            Success = true
        });
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Dry_run_fills_simulation_and_audits_once()
    {
        await using var env = await Env.CreateAsync(mode: "dry");

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.True(result.Ok, result.Error?.Message);
        var data = result.Data!.Value;
        Assert.Equal("filled", data.GetProperty("status").GetString());
        Assert.True(data.GetProperty("simulated").GetBoolean());
        Assert.Equal(0, env.Gateway.PlaceCalls);

        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(env.CycleId, order.CycleId);
        Assert.Equal(TraderId, order.TraderId);
        Assert.Equal(AccountId, order.ExchangeAccountId);
        Assert.Equal(env.ApprovedId, order.ProposalId);
        Assert.Equal(env.VerdictId, order.VerdictId);
        Assert.Equal("open", order.Action);
        Assert.Equal("long", order.Side);
        Assert.Equal("BTC/USDT", order.Symbol);
        Assert.Equal(0.4m, order.Quantity);
        Assert.Equal(100.05m, order.Price); // 最新价 100 × (1 + 0.05% 滑点)
        Assert.Equal(0.4m, order.FilledQuantity);
        Assert.Equal(100.05m, order.AveragePrice);
        Assert.Equal(4m, order.Leverage);
        Assert.Equal("filled", order.Status);
        Assert.True(order.Simulated);
        Assert.True(order.Success);
        Assert.Null(order.ExchangeOrderId);
        Assert.Contains("手续费", order.Note);
        Assert.Equal(ClientOrderId(env.CycleId, env.ApprovedId), order.ClientOrderId);

        var invocation = Assert.Single(await env.Db.ToolInvocations.ToListAsync());
        Assert.Equal("place_order", invocation.ToolName);
        Assert.Equal(env.CycleId, invocation.CycleId);
        Assert.Equal(env.StageRunId, invocation.StageRunId);
        Assert.True(invocation.Success);
        Assert.Null(invocation.ErrorCode);
        Assert.Contains(env.ApprovedId, invocation.ArgumentsJson);
        Assert.Contains("clientOrderId", invocation.ResultJson);
        Assert.Contains("filled", invocation.ResultJson);
        Assert.DoesNotContain("secret-api-key", invocation.ArgumentsJson);
        Assert.DoesNotContain("secret-api-secret", invocation.ArgumentsJson);
        Assert.DoesNotContain("secret-api-key", invocation.ResultJson);
        Assert.DoesNotContain("secret-api-secret", invocation.ResultJson);
        Assert.Equal(new[] { "executor" }, env.Budget.Roles);
        Assert.Equal(1, env.Accounts.AccountCalls);
        Assert.Equal(1, env.Accounts.PositionCalls);
    }

    [Fact]
    public async Task Dry_fill_applies_configured_slippage_and_fee()
    {
        await using var env = await Env.CreateAsync(mode: "dry");
        env.Config.RiskControl.EstimatedSlippagePct = 0.5m;
        env.Config.RiskControl.EstimatedFeePct = 0.2m;
        env.Market.LastPrice = 100m;

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.True(result.Ok, result.Error?.Message);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(100.5m, order.AveragePrice); // 最新价 × (1 + 0.5%)
        Assert.Equal(0.4m, order.FilledQuantity);
        Assert.Contains("滑点 0.5", order.Note);
        Assert.Contains("手续费 0.0804", order.Note);
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Live_order_passes_client_order_id_and_persists_exchange_order_id()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.ExchangeOrderId = "EX-123";
        env.Gateway.FilledQuantity = 0.4m;
        env.Gateway.AveragePrice = 100m;

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("EX-123", result.Data!.Value.GetProperty("exchangeOrderId").GetString());
        Assert.Equal(1, env.Gateway.PlaceCalls);
        var call = env.Gateway.LastPlace!;
        Assert.Equal("BTC", call.Symbol.BaseAsset);
        Assert.True(call.IsLong);
        Assert.Equal(0.4m, call.Quantity);
        Assert.Equal(95m, call.StopLossPrice);
        Assert.Equal(110m, call.TakeProfitPrice);
        Assert.Equal(4m, call.Leverage);
        Assert.Equal("limit", call.OrderType);
        Assert.Equal(100m, call.Price);
        Assert.Equal(ClientOrderId(env.CycleId, env.ApprovedId), call.ClientOrderId);

        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(call.ClientOrderId, order.ClientOrderId);
        Assert.Equal("EX-123", order.ExchangeOrderId);
        Assert.Equal("filled", order.Status);
        Assert.False(order.Simulated);
        Assert.True(order.Success);
        Assert.Equal(0.4m, order.FilledQuantity);
        Assert.Equal(100m, order.AveragePrice);

        var invocation = Assert.Single(await env.Db.ToolInvocations.ToListAsync());
        Assert.True(invocation.Success);
        Assert.Equal(new[] { "executor" }, env.Budget.Roles);
    }

    [Fact]
    public async Task Zero_filled_quantity_is_submitted_not_partial()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.FilledQuantity = 0m;
        env.Gateway.AveragePrice = null;

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.True(result.Ok, result.Error?.Message);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("submitted", order.Status);
        Assert.NotEqual("partial", order.Status);
        Assert.True(order.Success);
    }

    [Fact]
    public async Task Repeat_submission_returns_existing_order_without_reaching_exchange()
    {
        await using var env = await Env.CreateAsync(mode: "dry");

        var first = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);
        var second = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(second.Ok, second.Error?.Message);
        Assert.Equal(first.Data!.Value.GetProperty("orderId").GetInt32(),
            second.Data!.Value.GetProperty("orderId").GetInt32());
        Assert.Single(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.PlaceCalls);
        Assert.Equal(2, (await env.Db.ToolInvocations.ToListAsync()).Count);
    }

    [Fact]
    public async Task Parallel_calls_are_serialized_by_the_account_lock()
    {
        await using var env = await Env.CreateAsync(mode: "live");

        var results = await Task.WhenAll(
            env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
                0.4m, 100m, 4m, 95m, 110m, "test", default),
            env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
                0.4m, 100m, 4m, 95m, 110m, "test", default));

        Assert.All(results, result => Assert.True(result.Ok, result.Error?.Message));
        Assert.Equal(1, env.Gateway.PlaceCalls);
        Assert.Single(await env.Db.TraderOrders.AsNoTracking().ToListAsync());
        Assert.Equal(2, (await env.Db.ToolInvocations.AsNoTracking().ToListAsync()).Count);
    }

    [Fact]
    public async Task Two_failed_attempts_exhaust_the_proposal()
    {
        await using var env = await Env.CreateAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            env.Db.TraderOrders.Add(new TraderOrder
            {
                TraderId = TraderId,
                At = DateTime.UtcNow,
                Symbol = "BTC/USDT",
                Action = "open",
                Side = "long",
                Quantity = 0.4m,
                Price = 100m,
                CycleId = env.CycleId,
                ExchangeAccountId = AccountId,
                ProposalId = env.ApprovedId,
                VerdictId = env.VerdictId,
                ClientOrderId = ClientOrderId(env.CycleId, env.ApprovedId, attempt),
                Status = "failed",
                Success = false
            });
        }
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderLimitReached, result.Error!.Code);
        Assert.Null(env.Gateway.LastPlace);
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Unknown_submission_blocks_retry()
    {
        await using var env = await Env.CreateAsync();
        env.Db.TraderOrders.Add(new TraderOrder
        {
            TraderId = TraderId,
            At = DateTime.UtcNow,
            Symbol = "BTC/USDT",
            Action = "open",
            Side = "long",
            Quantity = 0.4m,
            Price = 100m,
            CycleId = env.CycleId,
            ExchangeAccountId = AccountId,
            ProposalId = env.ApprovedId,
            VerdictId = env.VerdictId,
            ClientOrderId = ClientOrderId(env.CycleId, env.ApprovedId, 0),
            Status = "unknown",
            Success = false
        });
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Unknown_order_blocks_different_proposal_and_cycle()
    {
        await using var env = await Env.CreateAsync();
        env.Db.TraderOrders.Add(new TraderOrder
        {
            TraderId = TraderId,
            At = DateTime.UtcNow,
            Symbol = "BTC/USDT",
            Action = "open",
            Side = "long",
            Quantity = 0.4m,
            Price = 100m,
            CycleId = Ids.NewUlid(), // 另一个周期
            ExchangeAccountId = AccountId,
            ProposalId = env.UnapprovedId, // 另一个提案
            VerdictId = env.VerdictId,
            ClientOrderId = ClientOrderId(env.CycleId, env.UnapprovedId, 0),
            Status = "unknown",
            Success = false
        });
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.Contains("结果未知的开仓", result.Error.Message);
        Assert.Equal(0, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Exchange_rejection_is_persisted_as_failed_without_simulation()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.Outcome = PlaceOutcome.NotCreated;
        env.Gateway.Error = "exchange rejected";

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeError, result.Error!.Code);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("failed", order.Status);
        Assert.False(order.Simulated);
        Assert.False(order.Success);
        Assert.Null(order.ExchangeOrderId);
        Assert.Equal("交易所拒绝下单，订单未创建", order.Error);
    }

    [Fact]
    public async Task Unknown_outcome_is_persisted_as_unknown_and_blocks_retry()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.Outcome = PlaceOutcome.Unknown;
        env.Gateway.Error = "下单超时";

        var first = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(first.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, first.Error!.Code);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("unknown", order.Status);
        Assert.False(order.Success);
        Assert.Equal("交易所结果未知，请人工核对", order.Error);

        var retry = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);
        Assert.False(retry.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, retry.Error!.Code);
        Assert.Equal(1, env.Gateway.PlaceCalls); // 未知结果不得重试
        Assert.Single(await env.Db.TraderOrders.ToListAsync());
    }

    [Fact]
    public async Task Gateway_exception_is_persisted_as_unknown()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.PlaceException = new TimeoutException("request timed out");

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("unknown", order.Status);
        Assert.False(order.Success);
        Assert.Equal(1, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Cancellation_after_gateway_keeps_unknown_order_row()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.Outcome = PlaceOutcome.Unknown;
        using var cts = new CancellationTokenSource();
        env.Gateway.OnPlace = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Tools.PlaceOrderAsync(
            env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", cts.Token));

        var order = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("unknown", order.Status);
        Assert.False(order.Success);
        Assert.Equal(1, env.Gateway.PlaceCalls);
        Assert.Empty(await env.Db.ToolInvocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Save_failure_after_gateway_keeps_unknown_order_row()
    {
        var interceptor = new FailSaveInterceptor();
        await using var env = await Env.CreateAsync(mode: "live", interceptor: interceptor);
        interceptor.FailAfter(1); // 放行 unknown 占位行，第二次（结果回写）失败

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        var order = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("unknown", order.Status);
        Assert.False(order.Success);
        Assert.Equal(1, env.Gateway.PlaceCalls);
    }

    [Fact]
    public async Task Gateway_error_text_is_not_forwarded()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.Outcome = PlaceOutcome.NotCreated;
        env.Gateway.Error =
            "POST https://api.binance.com/fapi/v1/order failed; apiKey=secret-api-key; SELECT * FROM accounts";

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeError, result.Error!.Code);
        Assert.DoesNotContain("http", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Error.Message.Length <= 200);

        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("交易所拒绝下单，订单未创建", order.Error);
        Assert.True(order.Error!.Length <= 500);
    }

    [Fact]
    public async Task Unique_client_order_id_race_returns_existing_order_without_second_submit()
    {
        var interceptor = new RaceInterceptor();
        await using var env = await Env.CreateAsync(mode: "live", interceptor: interceptor);
        var competingId = 0;
        interceptor.BeforeSave = async () =>
        {
            await using var other = new AppDbContext(env.Options);
            var competing = new TraderOrder
            {
                TraderId = TraderId,
                At = DateTime.UtcNow,
                Symbol = "BTC/USDT",
                Action = "open",
                Side = "long",
                Quantity = 0.4m,
                Price = 100m,
                CycleId = env.CycleId,
                ExchangeAccountId = AccountId,
                ProposalId = env.ApprovedId,
                VerdictId = env.VerdictId,
                ClientOrderId = ClientOrderId(env.CycleId, env.ApprovedId),
                Status = "unknown",
                Success = false
            };
            other.TraderOrders.Add(competing);
            await other.SaveChangesAsync();
            competingId = competing.Id;
        };

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(competingId, result.Data!.Value.GetProperty("orderId").GetInt32());
        Assert.Equal("unknown", result.Data.Value.GetProperty("status").GetString());
        Assert.Equal(0, env.Gateway.PlaceCalls);
        Assert.Single(await env.Db.TraderOrders.AsNoTracking().ToListAsync());
    }

    // —— close_position ——

    [Fact]
    public async Task Close_without_position_returns_position_not_found()
    {
        await using var env = await Env.CreateAsync();

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.PositionNotFound, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Close_does_not_require_risk_verdict()
    {
        await using var env = await Env.CreateAsync(mode: "dry", positions: [Position()]);
        env.Db.RiskVerdicts.RemoveRange(await env.Db.RiskVerdicts.ToListAsync());
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Empty(await env.Db.RiskVerdicts.ToListAsync());
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Close_partial_limit_persists_requested_quantity_and_exchange_order_id()
    {
        await using var env = await Env.CreateAsync(mode: "live", positions: [Position()]);

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", 0.4m, "limit", 100m, "manual", default);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, env.Gateway.CloseCalls);
        var call = env.Gateway.LastClose!;
        Assert.Equal("BTC", call.Symbol.BaseAsset);
        Assert.True(call.IsLong);
        Assert.Equal(0.4m, call.Quantity);
        Assert.Equal("limit", call.OrderType);
        Assert.Equal(100m, call.Price);
        Assert.NotNull(call.ClientOrderId);

        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(env.CycleId, order.CycleId);
        Assert.Equal(TraderId, order.TraderId);
        Assert.Equal(AccountId, order.ExchangeAccountId);
        Assert.Equal("close", order.Action);
        Assert.Equal("long", order.Side);
        Assert.Equal(0.4m, order.Quantity);
        Assert.Equal("EX-CLOSE", order.ExchangeOrderId);
        Assert.Equal("submitted", order.Status); // 限价平仓已受理未成交，仍可撤单
        Assert.Null(order.FilledQuantity); // 提交数量不是成交
        Assert.Null(order.AveragePrice);   // 参考价不是成交均价
        Assert.False(order.Simulated);
        Assert.True(order.Success);
        Assert.Equal(call.ClientOrderId, order.ClientOrderId);
        Assert.Null(order.ProposalId);
        Assert.Null(order.VerdictId);
    }

    [Fact]
    public async Task Close_live_market_persists_submitted_order_without_fabricated_fills()
    {
        await using var env = await Env.CreateAsync(mode: "live", positions: [Position()]);
        env.Gateway.CloseExchangeOrderId = "EX-FULL";
        env.Gateway.ClosePrice = 99.5m;

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, env.Gateway.CloseCalls);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("submitted", order.Status); // 交易所受理但无成交回报
        Assert.Equal("EX-FULL", order.ExchangeOrderId);
        Assert.Equal(100m, order.Price); // 参考价（报价），不是成交价
        Assert.Null(order.FilledQuantity);
        Assert.Null(order.AveragePrice);
        Assert.True(order.Success);
    }

    [Fact]
    public async Task Close_limit_price_beyond_two_percent_is_rejected()
    {
        await using var env = await Env.CreateAsync(positions: [Position()]);
        env.Gateway.Price = 100m;

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "limit", 102.01m, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.PriceOutOfBand, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Close_limit_price_within_two_percent_is_accepted()
    {
        await using var env = await Env.CreateAsync(mode: "dry", positions: [Position()]);
        env.Gateway.Price = 100m;

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "limit", 102m, "manual", default);

        Assert.True(result.Ok, result.Error?.Message);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(102m, order.Price);
        Assert.Equal(102m, order.AveragePrice);
    }

    [Fact]
    public async Task Close_dry_limit_fills_at_slippage_price_not_at_limit()
    {
        await using var env = await Env.CreateAsync(mode: "dry", positions: [Position()]);
        env.Gateway.Price = 100m;
        env.Config.RiskControl.EstimatedSlippagePct = 0.5m;

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "limit", 99.4m, "manual", default);

        Assert.True(result.Ok, result.Error?.Message);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(99.4m, order.Price);        // 委托价仍是限价
        Assert.Equal(99.5m, order.AveragePrice); // 100 × (1 - 0.5%)，未越限价
        Assert.Equal(1m, order.FilledQuantity);
    }

    [Fact]
    public async Task Close_dry_short_limit_caps_simulated_fill_at_limit()
    {
        await using var env = await Env.CreateAsync(mode: "dry",
            positions: [Position(side: SharedPositionSide.Short)]);
        env.Gateway.Price = 100m;
        env.Config.RiskControl.EstimatedSlippagePct = 0.5m;

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "limit", 100.4m, "manual", default);

        Assert.True(result.Ok, result.Error?.Message);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(100.4m, order.Price);
        Assert.Equal(100.4m, order.AveragePrice); // 买入模拟价 100.5 被限价 100.4 截断
        Assert.Equal("short", order.Side);
    }

    [Fact]
    public async Task Close_stale_market_data_is_rejected()
    {
        await using var env = await Env.CreateAsync(positions: [Position()],
            marketAsOf: DateTime.UtcNow.AddMinutes(-4));

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.StaleMarketData, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Close_with_cycle_ownership_mismatch_is_rejected()
    {
        await using var env = await Env.CreateAsync(mode: "dry", positions: [Position()]);
        var cycle = await env.Db.TradingCycles.SingleAsync();
        cycle.Mode = "live";
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Accepted_limit_close_can_be_cancelled_by_exchange_order_id()
    {
        await using var env = await Env.CreateAsync(mode: "live", positions: [Position()]);
        env.Gateway.CloseExchangeOrderId = "EX-LIMIT";

        var close = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "limit", 100m, "manual", default);
        var cancel = await env.Tools.CancelOrderAsync("EX-LIMIT", default);

        Assert.True(close.Ok, close.Error?.Message);
        Assert.True(cancel.Ok, cancel.Error?.Message);
        Assert.Equal(1, env.Gateway.CancelCalls);
        var order = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("cancelled", order.Status);
    }

    [Fact]
    public async Task Close_dry_run_fills_simulated_order_and_audits_once()
    {
        await using var env = await Env.CreateAsync(mode: "dry", positions: [Position()]);

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);

        Assert.True(result.Ok, result.Error?.Message);
        var data = result.Data!.Value;
        Assert.Equal("filled", data.GetProperty("status").GetString());
        Assert.True(data.GetProperty("simulated").GetBoolean());
        Assert.Equal(0, env.Gateway.CloseCalls);

        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(env.CycleId, order.CycleId);
        Assert.Equal(TraderId, order.TraderId);
        Assert.Equal(AccountId, order.ExchangeAccountId);
        Assert.Equal("close", order.Action);
        Assert.Equal("long", order.Side);
        Assert.Equal("BTC/USDT", order.Symbol);
        Assert.Equal(1m, order.Quantity);
        Assert.Equal(100m, order.Price);
        Assert.Equal(1m, order.FilledQuantity);
        Assert.Equal(99.95m, order.AveragePrice); // 最新价 100 减去 0.05% 滑点
        Assert.Equal(5m, order.Leverage);
        Assert.Equal("filled", order.Status);
        Assert.True(order.Simulated);
        Assert.True(order.Success);
        Assert.Null(order.ExchangeOrderId);
        Assert.Null(order.ProposalId);
        Assert.Null(order.VerdictId);

        var invocation = Assert.Single(await env.Db.ToolInvocations.ToListAsync());
        Assert.Equal("close_position", invocation.ToolName);
        Assert.True(invocation.Success);
        Assert.Equal(new[] { "executor" }, env.Budget.Roles);
    }

    [Fact]
    public async Task Close_proposal_must_be_close_action()
    {
        await using var env = await Env.CreateAsync(positions: [Position()]);

        // 夹具中的提案是 action=open，即使属于本周期也不得作为平仓提案。
        var result = await env.Tools.ClosePositionAsync(env.ApprovedId, "BTC/USDT", null, "market", null,
            "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Close_proposal_from_another_cycle_is_rejected()
    {
        await using var env = await Env.CreateAsync(positions: [Position()]);
        var proposal = CloseProposal(Ids.NewUlid());
        env.Db.TradeProposals.Add(proposal);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.ClosePositionAsync(proposal.Id, "BTC/USDT", null, "market", null,
            "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Close_with_matching_proposal_links_proposal_id()
    {
        await using var env = await Env.CreateAsync(mode: "dry", positions: [Position()]);
        var proposal = CloseProposal(env.CycleId);
        env.Db.TradeProposals.Add(proposal);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.ClosePositionAsync(proposal.Id, "BTC/USDT", null, "market", null,
            "manual", default);

        Assert.True(result.Ok, result.Error?.Message);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal(proposal.Id, order.ProposalId);
        Assert.Null(order.VerdictId);
    }

    [Fact]
    public async Task Repeated_close_proposal_returns_existing_order_without_second_submit()
    {
        await using var env = await Env.CreateAsync(mode: "dry", positions: [Position()]);
        var proposal = CloseProposal(env.CycleId);
        env.Db.TradeProposals.Add(proposal);
        await env.Db.SaveChangesAsync();

        var first = await env.Tools.ClosePositionAsync(proposal.Id, "BTC/USDT", null, "market", null,
            "manual", default);
        var second = await env.Tools.ClosePositionAsync(proposal.Id, "BTC/USDT", null, "market", null,
            "manual", default);

        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(second.Ok, second.Error?.Message);
        Assert.Equal(first.Data!.Value.GetProperty("orderId").GetInt32(),
            second.Data!.Value.GetProperty("orderId").GetInt32());
        Assert.Single(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Close_quantity_above_position_is_rejected()
    {
        await using var env = await Env.CreateAsync(positions: [Position(quantity: 1m)]);

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", 1.5m, "market", null, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.QuantityExceeded, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
        Assert.Equal(0, env.Gateway.CloseCalls);
    }

    [Fact]
    public async Task Close_exchange_rejection_is_persisted_as_failed()
    {
        await using var env = await Env.CreateAsync(mode: "live", positions: [Position()]);
        env.Gateway.CloseResultOutcome = CloseOutcome.Rejected;
        env.Gateway.CloseError = "rejected by exchange";

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeError, result.Error!.Code);
        Assert.DoesNotContain("rejected by exchange", result.Error.Message);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("failed", order.Status);
        Assert.False(order.Success);
        Assert.Null(order.ExchangeOrderId);
        Assert.Null(order.FilledQuantity);
        Assert.Null(order.AveragePrice);
        Assert.Equal("交易所拒绝平仓，订单未创建", order.Error);
    }

    [Fact]
    public async Task Close_timeout_result_is_persisted_unknown_and_not_retryable()
    {
        // 真实网关把超时收敛为 CloseResult.Unknown("平仓超时")，而不是抛异常。
        await using var env = await Env.CreateAsync(mode: "live", positions: [Position()]);
        env.Gateway.CloseResultOutcome = CloseOutcome.Unknown;
        env.Gateway.CloseError = "平仓超时";

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", 0.4m, "market", null, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Equal(1, env.Gateway.CloseCalls);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("unknown", order.Status);
        Assert.False(order.Success);
        Assert.Null(order.ExchangeOrderId);
        Assert.Null(order.FilledQuantity);
        Assert.Null(order.AveragePrice);
        Assert.Equal("平仓结果未知，请人工核对", order.Error);
    }

    [Fact]
    public async Task Close_accepted_limit_without_exchange_order_id_is_unknown()
    {
        await using var env = await Env.CreateAsync(mode: "live", positions: [Position()]);
        env.Gateway.CloseExchangeOrderId = null;

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "limit", 100m, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.False(result.Error.Retryable);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("unknown", order.Status);
        Assert.False(order.Success);
        Assert.Null(order.ExchangeOrderId);
        Assert.Null(order.FilledQuantity);
        Assert.Null(order.AveragePrice);
    }

    [Fact]
    public async Task Close_after_unknown_close_on_same_symbol_is_blocked()
    {
        await using var env = await Env.CreateAsync(mode: "live", positions: [Position()]);
        env.Gateway.CloseResultOutcome = CloseOutcome.Unknown;
        env.Gateway.CloseError = "平仓超时";

        var first = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);
        env.Gateway.CloseResultOutcome = CloseOutcome.Accepted;
        var second = await env.Tools.ClosePositionAsync(null, "BTC/USDT", null, "market", null, "manual", default);

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, second.Error!.Code);
        Assert.False(second.Error.Retryable);
        Assert.Equal(1, env.Gateway.CloseCalls); // 未知结果禁止重试
        Assert.Single(await env.Db.TraderOrders.ToListAsync());
    }

    [Fact]
    public async Task Close_unknown_outcome_from_exception_is_persisted_unknown_and_not_retryable()
    {
        await using var env = await Env.CreateAsync(mode: "live", positions: [Position()]);
        env.Gateway.CloseException = new TimeoutException("close timeout");

        var result = await env.Tools.ClosePositionAsync(null, "BTC/USDT", 0.4m, "market", null, "manual", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Equal(1, env.Gateway.CloseCalls);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("unknown", order.Status);
        Assert.False(order.Success);
        Assert.Null(order.ExchangeOrderId);
        Assert.Null(order.FilledQuantity);
        Assert.Equal("平仓结果未知，请人工核对", order.Error);
    }

    // —— cancel_order ——

    [Fact]
    public async Task Cancel_unknown_order_is_rejected()
    {
        await using var env = await Env.CreateAsync();

        var result = await env.Tools.CancelOrderAsync("missing", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderNotCancellable, result.Error!.Code);
        Assert.Equal(0, env.Gateway.CancelCalls);
    }

    [Fact]
    public async Task Cancel_order_of_another_trader_is_rejected()
    {
        await using var env = await Env.CreateAsync();
        env.Db.TraderOrders.Add(Order(env, traderId: TraderId + 1));
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync("lc-cancel-1", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderNotCancellable, result.Error!.Code);
        Assert.Equal(0, env.Gateway.CancelCalls);
    }

    [Fact]
    public async Task Cancel_order_of_another_account_is_rejected()
    {
        await using var env = await Env.CreateAsync();
        env.Db.TraderOrders.Add(Order(env, accountId: AccountId + 1));
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync("lc-cancel-1", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderNotCancellable, result.Error!.Code);
        Assert.Equal(0, env.Gateway.CancelCalls);
    }

    [Theory]
    [InlineData("filled")]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    public async Task Cancel_order_with_terminal_status_is_rejected(string status)
    {
        await using var env = await Env.CreateAsync();
        var order = Order(env, status: status);
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ClientOrderId!, default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderNotCancellable, result.Error!.Code);
        Assert.Equal(0, env.Gateway.CancelCalls);
    }

    [Fact]
    public async Task Cancel_dry_run_marks_simulated_order_cancelled_without_exchange_call()
    {
        await using var env = await Env.CreateAsync(mode: "dry");
        var order = Order(env, status: "partial", exchangeOrderId: null, simulated: true);
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ClientOrderId!, default);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("cancelled", result.Data!.Value.GetProperty("status").GetString());
        Assert.Equal(0, env.Gateway.CancelCalls);
        var saved = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("cancelled", saved.Status);
        Assert.False(saved.Success);

        var invocation = Assert.Single(await env.Db.ToolInvocations.ToListAsync());
        Assert.Equal("cancel_order", invocation.ToolName);
        Assert.True(invocation.Success);
        Assert.Equal(new[] { "executor" }, env.Budget.Roles);
    }

    [Fact]
    public async Task Cancel_dry_run_cannot_cancel_live_order()
    {
        await using var env = await Env.CreateAsync(mode: "dry");
        var order = Order(env, status: "submitted", simulated: false, cycleId: null);
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ClientOrderId!, default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderNotCancellable, result.Error!.Code);
        Assert.Equal(0, env.Gateway.CancelCalls);
        var saved = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("submitted", saved.Status); // 不得改写实盘订单
    }

    [Fact]
    public async Task Cancel_live_cannot_cancel_simulated_order()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        var order = Order(env, status: "submitted", simulated: true, cycleId: null);
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ClientOrderId!, default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderNotCancellable, result.Error!.Code);
        Assert.Equal(0, env.Gateway.CancelCalls);
        var saved = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("submitted", saved.Status); // 不得改写演练订单
    }

    [Fact]
    public async Task Cancel_live_calls_gateway_and_persists_cancelled()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        var order = Order(env, status: "submitted");
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ExchangeOrderId!, default);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, env.Gateway.CancelCalls);
        var call = env.Gateway.LastCancel!;
        Assert.Equal("BTC", call.Symbol.BaseAsset);
        Assert.Equal("EX-CANCEL", call.OrderId);

        var saved = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("cancelled", saved.Status);
        Assert.False(saved.Success);
        Assert.Equal("cancelled", result.Data!.Value.GetProperty("status").GetString());

        var invocation = Assert.Single(await env.Db.ToolInvocations.ToListAsync());
        Assert.Equal("cancel_order", invocation.ToolName);
        Assert.True(invocation.Success);
    }

    [Fact]
    public async Task Cancel_live_without_exchange_order_id_is_rejected()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        var order = Order(env, status: "submitted", exchangeOrderId: null);
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ClientOrderId!, default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderNotCancellable, result.Error!.Code);
        Assert.Contains("缺少交易所订单号", result.Error.Message);
        Assert.Equal(0, env.Gateway.CancelCalls);
    }

    [Fact]
    public async Task Cancel_live_without_symbol_is_rejected_with_clear_message()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        var order = Order(env, status: "submitted", symbol: null);
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ExchangeOrderId!, default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.OrderNotCancellable, result.Error!.Code);
        Assert.Contains("缺少标的", result.Error.Message);
        Assert.DoesNotContain("缺少交易所订单号", result.Error.Message);
        Assert.Equal(0, env.Gateway.CancelCalls);
    }

    [Fact]
    public async Task Cancel_with_cycle_ownership_mismatch_is_rejected()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        var cycle = await env.Db.TradingCycles.SingleAsync();
        cycle.ExchangeAccountId = AccountId + 1;
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync("missing", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error!.Code);
        Assert.Equal(0, env.Gateway.CancelCalls);
    }

    [Fact]
    public async Task Cancel_live_rejection_keeps_order_submitted()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.CancelResultOutcome = CancelOutcome.Rejected;
        env.Gateway.CancelError = "rejected";
        var order = Order(env, status: "submitted");
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ExchangeOrderId!, default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeError, result.Error!.Code);
        Assert.Equal(1, env.Gateway.CancelCalls);
        var saved = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("submitted", saved.Status);
    }

    [Fact]
    public async Task Cancel_timeout_result_is_unknown_and_keeps_order_submitted()
    {
        // 真实网关把超时收敛为 CancelResult.Unknown("撤单超时")，而不是抛异常。
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.CancelResultOutcome = CancelOutcome.Unknown;
        env.Gateway.CancelError = "撤单超时";
        var order = Order(env, status: "submitted");
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ExchangeOrderId!, default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Equal(1, env.Gateway.CancelCalls);
        var saved = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("submitted", saved.Status); // 未知绝不标记 cancelled
    }

    [Fact]
    public async Task Cancel_gateway_exception_keeps_order_submitted()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.CancelException = new TimeoutException("cancel timeout");
        var order = Order(env, status: "submitted");
        env.Db.TraderOrders.Add(order);
        await env.Db.SaveChangesAsync();

        var result = await env.Tools.CancelOrderAsync(order.ExchangeOrderId!, default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeUnknown, result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Equal(1, env.Gateway.CancelCalls);
        var saved = await env.Db.TraderOrders.AsNoTracking().SingleAsync();
        Assert.Equal("submitted", saved.Status);
    }

    private static SymbolPosition Position(decimal quantity = 1m,
        SharedPositionSide side = SharedPositionSide.Long) =>
        new("BTC/USDT", side, quantity, 100m, 0m, null, 5m);

    private static TradeProposal CloseProposal(string cycleId) => new()
    {
        Id = Ids.NewUlid(),
        CycleId = cycleId,
        TraderId = TraderId,
        Action = "close",
        Symbol = "BTC/USDT",
        Thesis = "test close",
        RawJson = "{}"
    };

    private static TraderOrder Order(Env env, string status = "submitted", string? exchangeOrderId = "EX-CANCEL",
        int? traderId = null, int? accountId = null, bool simulated = false, string? symbol = "BTC/USDT",
        string? cycleId = null) => new()
    {
        TraderId = traderId ?? TraderId,
        At = DateTime.UtcNow,
        Symbol = symbol,
        Action = "open",
        Side = "long",
        Quantity = 0.4m,
        Price = 100m,
        CycleId = cycleId ?? env.CycleId,
        ExchangeAccountId = accountId ?? AccountId,
        ClientOrderId = "lc-cancel-1",
        ExchangeOrderId = exchangeOrderId,
        Status = status,
        Simulated = simulated,
        Success = status is "submitted" or "partial" or "filled"
    };

    private static SharedFuturesSymbol BuildSpec(
        decimal? maxTradeQuantity = 1_000m,
        decimal? minTradeQuantity = 0.001m,
        decimal? minNotionalValue = 5m,
        decimal? quantityStep = 0.001m,
        decimal? priceStep = 0.01m) => new(TradingMode.PerpetualLinear, "BTC", "USDT", "BTCUSDT", true)
    {
        MaxLongLeverage = 20m,
        MaxShortLeverage = 20m,
        MaxTradeQuantity = maxTradeQuantity,
        MinTradeQuantity = minTradeQuantity,
        MinNotionalValue = minNotionalValue,
        QuantityStep = quantityStep,
        PriceStep = priceStep
    };

    private static string ClientOrderId(string cycleId, string proposalId, int attempt = 0)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proposalId)))
            .ToLowerInvariant()[..8];
        return $"lc-{cycleId[^12..]}-{hash}-{attempt}";
    }

    // —— 测试替身 ——

    private sealed record PlaceCall(SharedSymbol Symbol, bool IsLong, decimal Quantity, decimal StopLossPrice,
        decimal? TakeProfitPrice, decimal Leverage, string OrderType, decimal? Price, string? ClientOrderId);

    private sealed record CloseCall(SharedSymbol Symbol, decimal? Quantity, bool IsLong, string OrderType,
        decimal? Price, string? ClientOrderId);

    private sealed record CancelCall(SharedSymbol Symbol, string OrderId);

    private sealed class FakeGateway : IExecutionGateway
    {
        public TradingAudit AuditResult { get; set; } = new(true, true, true, true, true, false, []);
        public SharedFuturesSymbol? Spec { get; set; } = BuildSpec();
        public decimal? Price { get; set; } = 100m;
        public PlaceOutcome Outcome { get; set; } = PlaceOutcome.Created;
        public string? Error { get; set; }
        public Exception? PlaceException { get; set; }
        public Action? OnPlace { get; set; }
        public string? ExchangeOrderId { get; set; } = "EX-123";
        public decimal? FilledQuantity { get; set; } = 0.4m;
        public decimal? AveragePrice { get; set; } = 100m;
        public int PlaceCalls { get; private set; }
        public PlaceCall? LastPlace { get; private set; }
        public CloseOutcome CloseResultOutcome { get; set; } = CloseOutcome.Accepted;
        public string? CloseError { get; set; }
        public Exception? CloseException { get; set; }
        public decimal? CloseQuantity { get; set; }
        public decimal? ClosePrice { get; set; } = 100m;
        public string? CloseExchangeOrderId { get; set; } = "EX-CLOSE";
        public int CloseCalls { get; private set; }
        public CloseCall? LastClose { get; private set; }
        public CancelOutcome CancelResultOutcome { get; set; } = CancelOutcome.Accepted;
        public string? CancelError { get; set; }
        public Exception? CancelException { get; set; }
        public int CancelCalls { get; private set; }
        public CancelCall? LastCancel { get; private set; }

        public Task<decimal?> GetPriceAsync(SharedSymbol symbol, CancellationToken ct) =>
            Task.FromResult(Price);

        public Task<SharedFuturesSymbol?> GetFuturesSpecAsync(SharedSymbol symbol, CancellationToken ct) =>
            Task.FromResult(Spec);

        public Task<(bool Ok, string? Error)> SetLeverageAsync(SharedSymbol symbol, decimal leverage,
            CancellationToken ct) => Task.FromResult((true, (string?)null));

        public Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct) =>
            Task.FromResult<SharedPositionMode?>(SharedPositionMode.HedgeMode);

        public Task<PlaceResult> PlaceAsync(
            SharedSymbol symbol, bool isLong, decimal quantity, decimal stopLossPrice, decimal? takeProfitPrice,
            decimal leverage, string orderType, decimal? price, string? clientOrderId, CancellationToken ct)
        {
            PlaceCalls++;
            LastPlace = new PlaceCall(symbol, isLong, quantity, stopLossPrice, takeProfitPrice, leverage,
                orderType, price, clientOrderId);
            OnPlace?.Invoke();
            if (PlaceException is not null) throw PlaceException;
            return Task.FromResult(Outcome switch
            {
                PlaceOutcome.Created => PlaceResult.Created(ExchangeOrderId, FilledQuantity, AveragePrice),
                PlaceOutcome.NotCreated => PlaceResult.NotCreated(Error),
                _ => PlaceResult.Unknown(Error)
            });
        }

        public Task<CloseResult> CloseAsync(
            SharedSymbol symbol, decimal? quantity, bool isLong, string orderType, decimal? price,
            string? clientOrderId, CancellationToken ct)
        {
            CloseCalls++;
            LastClose = new CloseCall(symbol, quantity, isLong, orderType, price, clientOrderId);
            if (CloseException is not null) throw CloseException;
            return Task.FromResult(CloseResultOutcome switch
            {
                CloseOutcome.Accepted => CloseResult.Accepted(CloseExchangeOrderId,
                    CloseQuantity ?? quantity, ClosePrice),
                CloseOutcome.Rejected => CloseResult.Rejected(CloseError ?? "close failed"),
                _ => CloseResult.Unknown(CloseError)
            });
        }

        public Task<CancelResult> CancelAsync(SharedSymbol symbol, string orderId,
            CancellationToken ct)
        {
            CancelCalls++;
            LastCancel = new CancelCall(symbol, orderId);
            if (CancelException is not null) throw CancelException;
            return Task.FromResult(CancelResultOutcome switch
            {
                CancelOutcome.Accepted => CancelResult.Accepted(),
                CancelOutcome.Rejected => CancelResult.Rejected(CancelError ?? "cancel failed"),
                _ => CancelResult.Unknown(CancelError)
            });
        }

        public TradingAudit Audit() => AuditResult;
    }

    private sealed class FakeAccountReader : IAccountReader
    {
        public decimal Equity { get; set; } = 10_000m;
        public decimal Available { get; set; } = 9_000m;
        public bool AccountOk { get; set; } = true;
        public bool PositionsOk { get; set; } = true;
        public IReadOnlyList<SymbolPosition> Positions { get; set; } = [];
        public TradingDiscipline Discipline { get; set; } =
            new(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1), 0, false, true);
        public int AccountCalls { get; private set; }
        public int PositionCalls { get; private set; }

        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)
        {
            AccountCalls++;
            return Task.FromResult(new AccountSnapshot(AccountOk, Equity, Available, AccountOk ? null : "账户不可读"));
        }

        public Task<PositionReadResult> GetPositionsAsync(CancellationToken ct)
        {
            PositionCalls++;
            return Task.FromResult(new PositionReadResult(PositionsOk, Positions, PositionsOk ? null : "持仓不可读"));
        }

        public Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderOrder>>([]);

        public Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<PositionHistory>>([]);

        public Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TradeProposal>>([]);

        public Task<RiskControlConfig> GetRiskPolicyAsync(CancellationToken ct) =>
            Task.FromResult(new StrategyConfig().RiskControl);

        public Task<TradingDiscipline> GetDisciplineAsync(CancellationToken ct) =>
            Task.FromResult(Discipline);

        public Task<IReadOnlyList<TradingCycle>> GetCyclesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<TradingCycle>>([]);

        public Task<IReadOnlyList<TraderLesson>> GetLessonsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderLesson>>([]);
    }

    private sealed class FakeMarketReader : IMarketDataReader
    {
        public string Source { get; set; } = "live";
        public DateTime? AsOfUtc { get; set; } = DateTime.UtcNow;
        public decimal? LastPrice { get; set; } = 100m;

        public Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
            int bars, int warmupBars, CancellationToken ct) =>
            Task.FromResult(new MarketSnapshot(Source, $"{symbol.BaseAsset}/{symbol.QuoteAsset}",
                LastPrice, [], AsOfUtc: AsOfUtc));

        public Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) =>
            Task.FromResult<MarketSnapshot?>(null);

        public Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
            IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, SymbolSentiment>>(
                new Dictionary<string, SymbolSentiment>());

        public Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(IEnumerable<ExternalDataSource> sources,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalDataResult>>([]);

        public Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct) =>
            Task.FromResult<CoinSourceResolution?>(null);
    }

    private sealed class RecordingBudget : ICycleBudget
    {
        public List<string> Roles { get; } = [];

        public bool TryConsumeTool(string role)
        {
            Roles.Add(role);
            return true;
        }
    }

    private sealed class FailSaveInterceptor : SaveChangesInterceptor
    {
        private int _failAfter = -1;
        private int _saves;

        public void FailAfter(int saves)
        {
            _saves = 0;
            _failAfter = saves;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_failAfter >= 0 && ++_saves > _failAfter)
            {
                _failAfter = -1;
                throw new InvalidOperationException("forced save failure");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class RaceInterceptor : SaveChangesInterceptor
    {
        public Func<Task>? BeforeSave { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hook = BeforeSave;
            BeforeSave = null;
            if (hook is not null) await hook();
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Env : IAsyncDisposable
    {
        public required AppDbContext Db { get; init; }
        public required DbContextOptions<AppDbContext> Options { get; init; }
        public required FakeAccountReader Accounts { get; init; }
        public required FakeGateway Gateway { get; init; }
        public required FakeMarketReader Market { get; init; }
        public required RecordingBudget Budget { get; init; }
        public required StrategyConfig Config { get; init; }
        public required ExecutionTools Tools { get; init; }
        public required string CycleId { get; init; }
        public required string StageRunId { get; init; }
        public required string ApprovedId { get; init; }
        public required string UnapprovedId { get; init; }
        public required string VerdictId { get; init; }
        public required TradeProposal Proposal { get; init; }
        public required RiskVerdict Verdict { get; init; }
        public required SqliteConnection Connection { get; init; }

        public static async Task<Env> CreateAsync(
            string mode = "dry",
            decimal equity = 10_000m,
            decimal available = 9_000m,
            decimal approvedPct = 1m,
            decimal approvedLeverage = 5m,
            IReadOnlyList<SymbolPosition>? positions = null,
            decimal entryZoneMin = 95m,
            decimal entryZoneMax = 105m,
            bool allowMarket = true,
            DateTime? marketAsOf = null,
            string marketSource = "live",
            SaveChangesInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            var options = builder.Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var cycleId = Ids.NewUlid();
            var stageRunId = Ids.NewUlid();
            var approvedId = Ids.NewUlid();
            var unapprovedId = Ids.NewUlid();
            var verdictId = Ids.NewUlid();
            var now = DateTime.UtcNow;
            var cycle = new TradingCycle
            {
                Id = cycleId,
                TraderId = TraderId,
                StrategyId = StrategyId,
                ExchangeAccountId = AccountId,
                Mode = mode,
                Trigger = "test",
                Status = "running",
                StrategyConfigJson = "{}",
                MemoryFingerprint = "fp",
                StartedAt = now
            };
            var proposal = OpenProposal(approvedId, cycleId, now);
            var unapproved = OpenProposal(unapprovedId, cycleId, now);
            var verdict = new RiskVerdict
            {
                Id = verdictId,
                CycleId = cycleId,
                ProposalId = approvedId,
                Decision = "approved",
                ReasonsJson = "[]",
                MaxPositionPct = approvedPct,
                MaxLeverage = approvedLeverage,
                EntryZoneMin = entryZoneMin,
                EntryZoneMax = entryZoneMax,
                AllowMarket = allowMarket,
                ValidUntilUtc = now.AddMinutes(5),
                RawJson = "{}",
                CreatedAt = now
            };
            db.AddRange(cycle, proposal, unapproved, verdict);
            await db.SaveChangesAsync();

            var trader = new Trader
            {
                Id = TraderId,
                Name = "executor-test",
                ProviderId = 1,
                ExchangeAccountId = AccountId,
                StrategyId = StrategyId,
                ExecutionMode = mode,
                CreatedAt = now,
                UpdatedAt = now
            };
            var account = new ExchangeAccount
            {
                Id = AccountId,
                ExchangeType = ExchangeType.Binance,
                AccountName = "main",
                ApiKey = "secret-api-key",
                ApiSecret = "secret-api-secret",
                CreatedAt = now,
                UpdatedAt = now
            };
            var config = new StrategyConfig();
            var accounts = new FakeAccountReader
            {
                Equity = equity,
                Available = available,
                Positions = positions ?? []
            };
            var gateway = new FakeGateway();
            var market = new FakeMarketReader
            {
                Source = marketSource,
                AsOfUtc = marketAsOf ?? now
            };
            var hub = new SseEventHub();
            var budget = new RecordingBudget();
            // 频道显式传入（R-11-7 单点决策的装配口径）：本夹具的事件一律发往交易员频道。
            var auditor = new ToolAuditor(db, hub, budget, cycleId, stageRunId, SseChannels.Trader(TraderId));
            var context = new ExecutionToolContext(cycleId, stageRunId, mode, "binance", "5m",
                trader, account, config, budget, SseChannels.Trader(TraderId));
            var tools = new ExecutionTools(db, context, accounts, gateway, new MarketDataCache(market),
                hub, auditor);

            return new Env
            {
                Db = db,
                Options = options,
                Accounts = accounts,
                Gateway = gateway,
                Market = market,
                Budget = budget,
                Config = config,
                Tools = tools,
                CycleId = cycleId,
                StageRunId = stageRunId,
                ApprovedId = approvedId,
                UnapprovedId = unapprovedId,
                VerdictId = verdictId,
                Proposal = proposal,
                Verdict = verdict,
                Connection = connection
            };
        }

        private static TradeProposal OpenProposal(string id, string cycleId, DateTime now) => new()
        {
            Id = id,
            CycleId = cycleId,
            TraderId = TraderId,
            Action = "open",
            Symbol = "BTC/USDT",
            Side = "long",
            Confidence = 0.72m,
            EntryPrice = 100m,
            StopLossPrice = 95m,
            TakeProfitPrice = 110m,
            PositionPct = 1m,
            Leverage = 5m,
            Thesis = "回踩确认后开多",
            RawJson = "{}",
            CreatedAt = now
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
