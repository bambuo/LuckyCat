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
        await using var env = await Env.CreateAsync(equity: 10_000m, approvedPct: 3m, approvedLeverage: 5m);

        var good = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default); // 0.4*100/4 = 10 <= 3%*10000 = 300
        Assert.True(good.Ok, good.Error?.Message);

        var bad = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            13m, 100m, 4m, 95m, 110m, "test", default); // 13*100/4 = 325 > 300
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
    public async Task Limit_price_outside_approved_zone_is_rejected()
    {
        await using var env = await Env.CreateAsync(entryZoneMin: 98m, entryZoneMax: 102m);

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 105m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.PriceOutOfBand, result.Error!.Code);
        Assert.Empty(await env.Db.TraderOrders.ToListAsync());
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
        Assert.Equal(100m, order.Price);
        Assert.Equal(0.4m, order.FilledQuantity);
        Assert.Equal(100m, order.AveragePrice);
        Assert.Equal(4m, order.Leverage);
        Assert.Equal("filled", order.Status);
        Assert.True(order.Simulated);
        Assert.True(order.Success);
        Assert.Null(order.ExchangeOrderId);
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
    public async Task Exchange_rejection_is_persisted_as_failed_without_simulation()
    {
        await using var env = await Env.CreateAsync(mode: "live");
        env.Gateway.PlaceOk = false;

        var result = await env.Tools.PlaceOrderAsync(env.ApprovedId, "BTC/USDT", "long", "limit",
            0.4m, 100m, 4m, 95m, 110m, "test", default);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrors.ExchangeError, result.Error!.Code);
        var order = await env.Db.TraderOrders.SingleAsync();
        Assert.Equal("failed", order.Status);
        Assert.False(order.Simulated);
        Assert.False(order.Success);
        Assert.Null(order.ExchangeOrderId);
        Assert.Equal("exchange rejected", order.Error);
    }

    private static string ClientOrderId(string cycleId, string proposalId, int attempt = 0)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proposalId)))
            .ToLowerInvariant()[..8];
        return $"lc-{cycleId[^12..]}-{hash}-{attempt}";
    }

    // —— 测试替身 ——

    private sealed record PlaceCall(SharedSymbol Symbol, bool IsLong, decimal Quantity, decimal StopLossPrice,
        decimal? TakeProfitPrice, decimal Leverage, string OrderType, decimal? Price, string? ClientOrderId);

    private sealed class FakeGateway : IExecutionGateway
    {
        public TradingAudit AuditResult { get; set; } = new(true, true, true, true, true, false, []);
        public SharedFuturesSymbol? Spec { get; set; } = new(TradingMode.PerpetualLinear, "BTC", "USDT", "BTCUSDT", true)
        {
            MaxLongLeverage = 20m,
            MaxShortLeverage = 20m,
            QuantityStep = 0.001m,
            MinTradeQuantity = 0.001m,
            MinNotionalValue = 5m,
            PriceStep = 0.01m
        };
        public decimal? Price { get; set; } = 100m;
        public bool PlaceOk { get; set; } = true;
        public string? ExchangeOrderId { get; set; } = "EX-123";
        public decimal? FilledQuantity { get; set; } = 0.4m;
        public decimal? AveragePrice { get; set; } = 100m;
        public int PlaceCalls { get; private set; }
        public PlaceCall? LastPlace { get; private set; }

        public Task<decimal?> GetPriceAsync(SharedSymbol symbol, CancellationToken ct) =>
            Task.FromResult(Price);

        public Task<SharedFuturesSymbol?> GetFuturesSpecAsync(SharedSymbol symbol, CancellationToken ct) =>
            Task.FromResult(Spec);

        public Task<(bool Ok, string? Error)> SetLeverageAsync(SharedSymbol symbol, decimal leverage,
            CancellationToken ct) => Task.FromResult((true, (string?)null));

        public Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct) =>
            Task.FromResult<SharedPositionMode?>(SharedPositionMode.HedgeMode);

        public Task<(bool Ok, string? Error, string? ExchangeOrderId, decimal? FilledQuantity, decimal? AveragePrice)> PlaceAsync(
            SharedSymbol symbol, bool isLong, decimal quantity, decimal stopLossPrice, decimal? takeProfitPrice,
            decimal leverage, string orderType, decimal? price, string? clientOrderId, CancellationToken ct)
        {
            PlaceCalls++;
            LastPlace = new PlaceCall(symbol, isLong, quantity, stopLossPrice, takeProfitPrice, leverage,
                orderType, price, clientOrderId);
            return Task.FromResult((PlaceOk, PlaceOk ? null : "exchange rejected",
                PlaceOk ? ExchangeOrderId : null, PlaceOk ? FilledQuantity : null, PlaceOk ? AveragePrice : null));
        }

        public Task<(bool Ok, string? Error, decimal? Quantity, decimal? Price, string? ExchangeOrderId)> CloseAsync(
            SharedSymbol symbol, decimal? quantity, bool isLong, string orderType, decimal? price,
            string? clientOrderId, CancellationToken ct) =>
            Task.FromResult<(bool Ok, string? Error, decimal? Quantity, decimal? Price, string? ExchangeOrderId)>(
                (false, "not supported", null, null, null));

        public Task<(bool Ok, string? Error)> CancelAsync(SharedSymbol symbol, string orderId,
            CancellationToken ct) =>
            Task.FromResult<(bool Ok, string? Error)>((false, "not supported"));

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

    private sealed class Env : IAsyncDisposable
    {
        public required AppDbContext Db { get; init; }
        public required FakeAccountReader Accounts { get; init; }
        public required FakeGateway Gateway { get; init; }
        public required RecordingBudget Budget { get; init; }
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
            decimal approvedPct = 3m,
            decimal approvedLeverage = 5m,
            IReadOnlyList<SymbolPosition>? positions = null,
            decimal entryZoneMin = 95m,
            decimal entryZoneMax = 105m,
            bool allowMarket = true,
            DateTime? marketAsOf = null,
            string marketSource = "live")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
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
            var auditor = new ToolAuditor(db, hub, budget, cycleId, stageRunId, TraderId);
            var context = new ExecutionToolContext(cycleId, stageRunId, mode, "binance", "5m",
                trader, account, config, budget);
            var tools = new ExecutionTools(db, context, accounts, gateway, new MarketDataCache(market),
                hub, auditor);

            return new Env
            {
                Db = db,
                Accounts = accounts,
                Gateway = gateway,
                Budget = budget,
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
