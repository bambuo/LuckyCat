using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using Microsoft.EntityFrameworkCore;

namespace LuckyCat.Tools;

/// <summary>
/// 执行员工具上下文（永久保留）：一个周期内绑定交易员、账户、策略快照与预算；
/// 工具只能访问该上下文，禁止模型传入账户/交易员/策略/密钥等归属参数。
/// </summary>
public sealed record ExecutionToolContext(
    string CycleId, string StageRunId, string Mode, string Exchange, string Timeframe,
    Trader Trader, ExchangeAccount Account, StrategyConfig Config, ICycleBudget Budget);

/// <summary>
/// 执行员写工具：<c>place_order</c> 按 LC-STD-02 §6 的 12 步硬闸执行，所有校验失败一律拒绝（fail-closed）。
/// 复用 <see cref="ToolAuditor"/> 落 ToolInvocation/预算/工具 SSE（工具内部不再写第二份审计），
/// 只在真实提交或演练成交后写 <see cref="TraderOrder"/> 并发布 order SSE。
/// 账户写锁按账户 ID 进程内串行，覆盖账户重读、全部校验、提交与订单落库。
/// </summary>
public sealed class ExecutionTools(
    AppDbContext db,
    ExecutionToolContext ctx,
    IAccountReader accounts,
    IExecutionGateway gateway,
    MarketDataCache market,
    SseEventHub hub,
    ToolAuditor audit)
{
    private const string Role = "executor";

    // 账户写锁：同一交易所账户的写工具串行执行（静态，跨周期共享；键为账户主键）。
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> AccountLocks = new();

    private readonly AppDbContext _db = db;
    private readonly ExecutionToolContext _ctx = ctx;
    private readonly StrategyConfig _config = ctx.Config;
    private readonly string _exchange = ctx.Exchange;
    private readonly IAccountReader _accounts = accounts;
    private readonly IExecutionGateway _gateway = gateway;
    private readonly MarketDataCache _market = market;
    private readonly SseEventHub _hub = hub;
    private readonly ToolAuditor _audit = audit;
    private int _attempt;

    [Description("在已批准提案的可执行边界内开仓；必须提供止损与止盈，参数必须与提案一致。")]
    public Task<ToolEnvelope> PlaceOrderAsync(
        [Description("本周期已获批提案 ID")] string proposalId,
        [Description("标的，必须等于提案标的（如 BTC/USDT）")] string symbol,
        [Description("方向：long | short，必须等于提案方向")] string side,
        [Description("订单类型：market | limit")] string orderType,
        [Description("数量，必须满足批准保证金与交易所精度")] decimal quantity,
        [Description("限价单价格，必须在批准价格带内")] decimal? price,
        [Description("杠杆，缺省取批准上限")] decimal? leverage,
        [Description("止损价，必须等于提案止损价")] decimal stopLossPrice,
        [Description("止盈价，必须等于提案止盈价")] decimal? takeProfitPrice,
        [Description("下单理由，审计用，≤300 字")] string reason,
        CancellationToken ct = default)
    {
        var request = new OrderRequest(proposalId, symbol, side, orderType, quantity, price, leverage,
            stopLossPrice, takeProfitPrice, reason);
        return _audit.RunAsync("place_order", Role, request, () => PlaceOrderCoreAsync(request, ct), ct);
    }

    /// <summary>一次 place_order 的不可变入参（也作为 ToolInvocation 的审计入参）。</summary>
    private sealed record OrderRequest(string ProposalId, string Symbol, string Side, string OrderType,
        decimal Quantity, decimal? Price, decimal? Leverage, decimal StopLossPrice,
        decimal? TakeProfitPrice, string Reason);

    private async Task<ToolEnvelope> PlaceOrderCoreAsync(OrderRequest request, CancellationToken ct)
    {
        // 1) 模式：只认 dry/live。
        if (_ctx.Mode is not ("dry" or "live")) return Fail(ToolErrors.InvalidArgument, "周期执行模式无效");

        // 入参自身的合法性与必填项（止盈为开仓硬性要求）。
        if (string.IsNullOrWhiteSpace(request.ProposalId)) return Fail(ToolErrors.InvalidArgument, "proposalId 必填");
        if (string.IsNullOrWhiteSpace(request.Symbol)) return Fail(ToolErrors.InvalidArgument, "symbol 必填");
        if (request.Side is not ("long" or "short")) return Fail(ToolErrors.InvalidArgument, "side 必须为 long 或 short");
        if (request.OrderType is not ("market" or "limit")) return Fail(ToolErrors.InvalidArgument, "orderType 无效");
        if (request.OrderType == "limit" && request.Price is not > 0)
            return Fail(ToolErrors.InvalidArgument, "限价单必须提供有效价格");
        if (request.Price is <= 0) return Fail(ToolErrors.InvalidArgument, "价格必须大于 0");
        if (request.Quantity <= 0) return Fail(ToolErrors.InvalidArgument, "数量必须大于 0");
        if (request.Leverage is <= 0) return Fail(ToolErrors.InvalidArgument, "杠杆必须大于 0");
        if (request.StopLossPrice <= 0) return Fail(ToolErrors.InvalidArgument, "止损价必须大于 0");
        if (request.TakeProfitPrice is not > 0) return Fail(ToolErrors.InvalidArgument, "止盈价必须大于 0");
        if (string.IsNullOrWhiteSpace(request.Reason)) return Fail(ToolErrors.InvalidArgument, "reason 必填");
        if (request.Reason.Length > 300) return Fail(ToolErrors.InvalidArgument, "reason 不得超过 300 字");

        // 2) 提案存在：本周期、本交易员、本策略（周期快照一并校验归属）。
        var cycle = await _db.TradingCycles.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == _ctx.CycleId, ct);
        if (cycle is null || cycle.TraderId != _ctx.Trader.Id || cycle.StrategyId != _ctx.Trader.StrategyId
            || cycle.ExchangeAccountId != _ctx.Account.Id || cycle.Mode != _ctx.Mode)
            return Fail(ToolErrors.ProposalNotFound, "周期归属不匹配");

        var proposal = await _db.TradeProposals.FindAsync([request.ProposalId], ct);
        if (proposal is null || proposal.CycleId != _ctx.CycleId
            || (proposal.TraderId is { } proposalTraderId && proposalTraderId != _ctx.Trader.Id))
            return Fail(ToolErrors.ProposalNotFound, "提案不存在或不属于本周期");
        if (proposal.Action != "open" || proposal.Symbol != request.Symbol || proposal.Side != request.Side)
            return Fail(ToolErrors.InvalidArgument, "开仓参数与提案不一致");
        if (proposal.EntryPrice is not > 0) return Fail(ToolErrors.ProposalNotApproved, "提案缺少有效入场价");
        if (proposal.Leverage is not > 0) return Fail(ToolErrors.ProposalNotApproved, "提案缺少有效杠杆");
        if (proposal.StopLossPrice != request.StopLossPrice)
            return Fail(ToolErrors.InvalidArgument, "止损价必须等于提案值");
        if (proposal.TakeProfitPrice != request.TakeProfitPrice)
            return Fail(ToolErrors.InvalidArgument, "止盈价必须等于提案值");

        // 3) 风控批准；4) 未过期（有效期缺失一律按过期处理）。
        var verdict = await _db.RiskVerdicts.AsNoTracking()
            .SingleOrDefaultAsync(v => v.ProposalId == request.ProposalId, ct);
        if (verdict is null || verdict.Decision != "approved" || verdict.CycleId != _ctx.CycleId)
            return Fail(ToolErrors.ProposalNotApproved, "提案未批准");
        if (verdict.ValidUntilUtc is not { } validUntil || DateTime.UtcNow > validUntil)
            return Fail(ToolErrors.ProposalExpired, "提案已过期");
        if (verdict.MaxPositionPct is not { } maxPositionPct || maxPositionPct <= 0
            || verdict.MaxLeverage is not { } maxLeverage || maxLeverage <= 0)
            return Fail(ToolErrors.ProposalNotApproved, "裁决缺少批准边界");

        // 5) 账户锁内重读账户与持仓，锁覆盖后续校验、提交与落库。
        var accountLock = AccountLocks.GetOrAdd(_ctx.Account.Id, _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(ct);
        try
        {
            return await PlaceOrderLockedAsync(request, proposal, verdict, maxPositionPct, maxLeverage, ct);
        }
        finally
        {
            accountLock.Release();
        }
    }

    private async Task<ToolEnvelope> PlaceOrderLockedAsync(OrderRequest request, TradeProposal proposal,
        RiskVerdict verdict, decimal maxPositionPct, decimal maxLeverage, CancellationToken ct)
    {
        // 5) 账户重读：不信任任何早先缓存。
        var account = await _accounts.GetAccountAsync(ct);
        var positions = await _accounts.GetPositionsAsync(ct);
        if (!account.Success || !positions.Success) return Fail(ToolErrors.DataUnavailable, "账户或持仓不可读", true);
        if (account.UsdtEquity <= 0) return Fail(ToolErrors.DataUnavailable, "账户权益不可用", true);

        // 已用保证金无法完整确认时拒绝开仓（不得用差额下界冒充真实占用）。
        var margin = MarginMath.Estimate(account, positions.Positions);
        if (!margin.Complete)
            return Fail(ToolErrors.DataUnavailable, "保证金无法确认（部分持仓缺杠杆或未实现盈亏），拒绝开仓", true);

        // 6) 杠杆、价格带与保证金校验（当前权益 + 本次提交杠杆）。
        var currentLeverage = request.Leverage ?? maxLeverage;
        if (currentLeverage > maxLeverage) return Fail(ToolErrors.LeverageExceeded, "杠杆超过批准上限");
        if (currentLeverage > proposal.Leverage!.Value)
            return Fail(ToolErrors.LeverageExceeded, "杠杆超过提案上限");
        if (currentLeverage <= 0) return Fail(ToolErrors.InvalidArgument, "杠杆必须大于 0");

        var shared = CoinRankingService.ParseConfiguredSymbol(request.Symbol);
        var executionPrice = request.OrderType == "limit"
            ? request.Price
            : await _gateway.GetPriceAsync(shared, ct);
        if (executionPrice is not > 0) return Fail(ToolErrors.DataUnavailable, "执行价格不可用", true);

        if (request.OrderType == "limit")
        {
            var limitPrice = request.Price!.Value;
            if (verdict.EntryZoneMin is not { } zoneMin || verdict.EntryZoneMax is not { } zoneMax
                || limitPrice < zoneMin || limitPrice > zoneMax)
                return Fail(ToolErrors.PriceOutOfBand, "限价超出批准价格带");
        }
        else
        {
            if (verdict.AllowMarket != true) return Fail(ToolErrors.InvalidArgument, "裁决不允许市价");
            var deviation = Math.Abs(executionPrice.Value / proposal.EntryPrice!.Value - 1m) * 100m;
            if (deviation > _config.RiskControl.MaxEntryDeviationPct)
                return Fail(ToolErrors.PriceOutOfBand, "市价偏离提案价超过允许范围");
        }

        var submittedMargin = request.Quantity * executionPrice.Value / currentLeverage;
        var approvedMargin = account.UsdtEquity * maxPositionPct / 100m;
        if (submittedMargin > approvedMargin) return Fail(ToolErrors.QuantityExceeded, "保证金超过批准上限");
        if (submittedMargin > account.UsdtAvailable) return Fail(ToolErrors.QuantityExceeded, "可用保证金不足");

        // 10) 交易所边界（合约规格未知任何一项都不得提交）。
        var spec = await _gateway.GetFuturesSpecAsync(shared, ct);
        if (spec is null) return Fail(ToolErrors.ExchangeUnknown, "合约规格未知，无法确认交易所能力");
        if (!spec.Trading) return Fail(ToolErrors.ExchangeUnknown, "标的当前不可交易");
        var exchangeMaxLeverage = request.Side == "long" ? spec.MaxLongLeverage : spec.MaxShortLeverage;
        if (exchangeMaxLeverage is not > 0) return Fail(ToolErrors.ExchangeUnknown, "交易所方向杠杆上限未知");
        if (currentLeverage > exchangeMaxLeverage.Value) return Fail(ToolErrors.LeverageExceeded, "杠杆超过交易所上限");
        if (spec.MaxTradeQuantity is { } maxTradeQuantity && request.Quantity > maxTradeQuantity)
            return Fail(ToolErrors.QuantityExceeded, "数量超过交易所上限");
        if (spec.QuantityStep is > 0 && request.Quantity % spec.QuantityStep.Value != 0)
            return Fail(ToolErrors.InvalidArgument, "数量精度不符合交易所步长");
        if (spec.MinTradeQuantity is { } minTradeQuantity && request.Quantity < minTradeQuantity)
            return Fail(ToolErrors.InvalidArgument, "数量低于交易所最小下单量");
        var notional = request.Quantity * executionPrice.Value;
        if (spec.MinNotionalValue is { } minNotional && notional < minNotional)
            return Fail(ToolErrors.InvalidArgument, "名义价值低于交易所最小下单金额");
        if (request.OrderType == "limit" && spec.PriceStep is > 0
            && request.Price!.Value % spec.PriceStep.Value != 0)
            return Fail(ToolErrors.InvalidArgument, "价格精度不符合交易所步长");

        // 7) 用最新账户/持仓/价格重跑风控。
        var discipline = await _accounts.GetDisciplineAsync(ct);
        var riskContext = new OpenRiskContext(account.UsdtEquity, account.UsdtAvailable,
            margin.UsedMargin, positions.Positions.Count,
            positions.Positions.Any(position => TradingRiskPolicy.BaseAsset(position.Symbol)
                == TradingRiskPolicy.BaseAsset(request.Symbol)),
            executionPrice.Value, exchangeMaxLeverage.Value, spec.QuantityStep ?? 0m,
            spec.MinTradeQuantity ?? 0m, spec.MinNotionalValue ?? 0m, discipline);
        if (ProposalRiskAdapter.ValidateOpen(proposal, _config.RiskControl, riskContext, out _) is { } riskError)
            return Fail(ToolErrors.RiskPolicyViolation, riskError);

        // 8) 行情新鲜度：执行标的采样不得超过 3 分钟。
        var fresh = await _market.GetSnapshotAsync(_exchange, shared, _ctx.Timeframe, 2, 0, ct);
        if (fresh.Source != "live" || fresh.AsOfUtc is not { } asOf
            || DateTime.UtcNow - asOf > TimeSpan.FromMinutes(3))
            return Fail(ToolErrors.StaleMarketData, "行情缺失或超过 3 分钟");

        // 9) 重复与上限：同一提案已成功提交则幂等返回；同标的 10 秒内/本周期同标的最多 1 笔。
        var existing = await _db.TraderOrders.AsNoTracking()
            .Where(o => o.CycleId == _ctx.CycleId && o.ProposalId == request.ProposalId
                && o.Action == "open" && o.Success)
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return ExistingOrder(existing);

        var now = DateTime.UtcNow;
        var recent = await _db.TraderOrders.AsNoTracking()
            .Where(o => o.ExchangeAccountId == _ctx.Account.Id && o.At >= now.AddSeconds(-10))
            .ToListAsync(ct);
        if (recent.Any(order => IsEffectiveOpen(order) && SameBaseAsset(order.Symbol, request.Symbol)))
            return Fail(ToolErrors.OrderLimitReached, "同一标的 10 秒内已提交开仓");

        var cycleOrders = await _db.TraderOrders.AsNoTracking()
            .Where(o => o.CycleId == _ctx.CycleId)
            .ToListAsync(ct);
        if (cycleOrders.Any(order => order.ProposalId == request.ProposalId && order.Status == "unknown"))
            return Fail(ToolErrors.ExchangeUnknown, "上一笔提交结果未知，禁止重试，请人工核对");
        if (cycleOrders.Any(order => IsEffectiveOpen(order) && SameBaseAsset(order.Symbol, request.Symbol)))
            return Fail(ToolErrors.OrderLimitReached, "本周期该标的已提交过开仓");

        // 同一提案最多 2 次提交尝试：第二次仅用于交易所明确未创建订单的失败。
        var submittedAttempts = await _db.TraderOrders.CountAsync(
            o => o.CycleId == _ctx.CycleId && o.ProposalId == request.ProposalId, ct);
        if (submittedAttempts >= 2) return Fail(ToolErrors.OrderLimitReached, "同一提案最多提交 2 次");

        // 尝试序号按已落库尝试数推进，避免同一周期内重启工具实例后复用旧 clientOrderId。
        var attempt = Math.Max(_attempt, submittedAttempts);
        _attempt = attempt + 1;
        var clientOrderId = ClientOrderId(_ctx.CycleId, request.ProposalId, attempt);
        if (await _db.TraderOrders.AnyAsync(
                o => o.CycleId == _ctx.CycleId && o.ClientOrderId == clientOrderId, ct))
            return Fail(ToolErrors.DuplicateOrder, "重复订单");

        // 10) 实盘能力：止损为硬性保护、止盈为硬性目标，任一未知/不支持都不提交；演练可模拟。
        if (_ctx.Mode == "live")
        {
            var capability = _gateway.Audit();
            if (!capability.CanTrade) return Fail(ToolErrors.ExchangeUnknown, "账户不具备实盘交易能力");
            if (!capability.StopLossSupported) return Fail(ToolErrors.ExchangeUnknown, "交易所不支持附加止损");
            if (!capability.TakeProfitSupported) return Fail(ToolErrors.ExchangeUnknown, "交易所不支持附加止盈");
        }

        // 11) 提交：演练只模拟成交，绝不调用下单；实盘透传工具生成的 clientOrderId。
        if (_ctx.Mode == "dry")
        {
            var simulated = NewOrder(clientOrderId, request, executionPrice.Value, currentLeverage, "filled",
                simulated: true, request.Quantity, executionPrice.Value, proposal.Id, verdict.Id, request.Reason);
            _db.TraderOrders.Add(simulated);
            await _db.SaveChangesAsync(ct);
            _hub.Publish(SseChannels.Trader(_ctx.Trader.Id), new SseEvent("order", simulated));
            return ToolEnvelope.Success(OrderData(simulated), DateTimeOffset.UtcNow, "live",
                ["演练模式：成交为模拟，不代表交易所真实成交"]);
        }

        var placed = await _gateway.PlaceAsync(shared, request.Side == "long", request.Quantity,
            request.StopLossPrice, request.TakeProfitPrice, currentLeverage, request.OrderType,
            request.OrderType == "limit" ? request.Price : null, clientOrderId, ct);
        var status = !placed.Ok
            ? placed.Error is null ? "unknown" : "failed"
            : placed.FilledQuantity is null ? "submitted"
            : placed.FilledQuantity >= request.Quantity ? "filled" : "partial";

        // 12) 落库与推送：实际提交（含失败/未知）都留痕；成功才返回成功回执。
        var row = NewOrder(clientOrderId, request, executionPrice.Value, currentLeverage, status,
            simulated: false, placed.FilledQuantity, placed.AveragePrice, proposal.Id, verdict.Id, request.Reason);
        row.ExchangeOrderId = placed.ExchangeOrderId;
        row.Error = Truncate(placed.Error, 500);
        _db.TraderOrders.Add(row);
        await _db.SaveChangesAsync(ct);
        _hub.Publish(SseChannels.Trader(_ctx.Trader.Id), new SseEvent("order", row));

        if (placed.Ok) return ToolEnvelope.Success(OrderData(row), DateTimeOffset.UtcNow, "live");
        return placed.Error is null
            ? Fail(ToolErrors.ExchangeUnknown, "交易所结果未知，请人工核对")
            : Fail(ToolErrors.ExchangeError, placed.Error);
    }

    private TraderOrder NewOrder(string clientOrderId, OrderRequest request, decimal price, decimal leverage,
        string status, bool simulated, decimal? filledQuantity, decimal? averagePrice,
        string proposalId, string verdictId, string reason) => new()
    {
        CycleId = _ctx.CycleId,
        TraderId = _ctx.Trader.Id,
        ExchangeAccountId = _ctx.Account.Id,
        ProposalId = proposalId,
        VerdictId = verdictId,
        ClientOrderId = clientOrderId,
        Action = "open",
        Side = request.Side,
        Symbol = request.Symbol,
        Quantity = request.Quantity,
        Price = price,
        StopLossPrice = request.StopLossPrice,
        TakeProfitPrice = request.TakeProfitPrice,
        FilledQuantity = filledQuantity,
        AveragePrice = averagePrice,
        Leverage = leverage,
        Status = status,
        Simulated = simulated,
        Success = status is "submitted" or "filled" or "partial",
        Note = Truncate(reason, 500),
        At = DateTime.UtcNow
    };

    private static ToolEnvelope ExistingOrder(TraderOrder order) =>
        ToolEnvelope.Success(OrderData(order), DateTimeOffset.UtcNow, "live",
            ["该提案已成功提交，返回已有订单"]);

    private static JsonElement OrderData(TraderOrder order) =>
        JsonSerializer.SerializeToElement(new
        {
            orderId = order.Id,
            clientOrderId = order.ClientOrderId,
            status = order.Status,
            simulated = order.Simulated,
            exchangeOrderId = order.ExchangeOrderId
        }, ApiJson.Options);

    private static bool IsEffectiveOpen(TraderOrder order) =>
        order.Action == "open" && (order.Success
            || order.Status is "submitted" or "partial" or "filled");

    private static bool SameBaseAsset(string? left, string right) =>
        left is not null
        && TradingRiskPolicy.BaseAsset(left) == TradingRiskPolicy.BaseAsset(right);

    /// <summary>LC-STD-02 §9：周期后 12 位 + 订单键 SHA256 前 8 位 + attempt，≤36 字符。</summary>
    private static string ClientOrderId(string cycleId, string orderKey, int attempt)
    {
        var tail = cycleId.Length <= 12 ? cycleId : cycleId[^12..];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(orderKey)))
            .ToLowerInvariant()[..8];
        return $"lc-{tail}-{hash}-{attempt}";
    }

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max];

    private static ToolEnvelope Fail(string code, string message, bool retryable = false) =>
        ToolEnvelope.Failure(code, message, retryable);
}
