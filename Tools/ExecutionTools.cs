using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CryptoExchange.Net.SharedApis;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using Microsoft.Data.Sqlite;
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
/// 账户写锁在审计包装器外层获取，覆盖完整调用（账户重读、全部校验、提交、订单落库与 ToolInvocation 落库）。
/// 单进程内按账户 ID 条纹串行（64 个信号量，容量有界）；跨进程互斥不在本设计范围，部署必须保持单进程。
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
    private const string ExchangeRejectedMessage = "交易所拒绝下单，订单未创建";
    private const string ExchangeUnknownMessage = "交易所结果未知，请人工核对";
    private const int MaxInternalErrorChars = 300;
    private const int AccountLockStripes = 64;

    // 账户写锁：同一交易所账户的完整写工具调用（含审计落库）串行执行。
    // 固定 64 条纹，避免按账户 ID 无限增长；不同账户偶发共用一个条纹只影响吞吐，不影响正确性。
    private static readonly SemaphoreSlim[] AccountLocks = CreateAccountLocks();

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
    public async Task<ToolEnvelope> PlaceOrderAsync(
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
        // 锁覆盖 action + ToolInvocation 落库：同一账户的写调用（含审计）完全串行，
        // 不允许「订单已提交但审计尚未落库」的窗口被并发调用穿过。
        var accountLock = AccountLock(_ctx.Account.Id);
        await accountLock.WaitAsync(ct);
        try
        {
            return await _audit.RunAsync("place_order", Role, request, () => PlaceOrderCoreAsync(request, ct), ct);
        }
        finally
        {
            accountLock.Release();
        }
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
        if (proposal.PositionPct is not > 0) return Fail(ToolErrors.ProposalNotApproved, "提案缺少有效仓位");
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

        // 风控只能收紧：批准边界不得超过提案本身。
        if (maxPositionPct > proposal.PositionPct.Value)
            return Fail(ToolErrors.RiskPolicyViolation, "裁决仓位上限超过提案，拒绝执行");
        if (maxLeverage > proposal.Leverage.Value)
            return Fail(ToolErrors.LeverageExceeded, "裁决杠杆上限超过提案，拒绝执行");

        // 5) 账户锁已由 PlaceOrderAsync 持有；此处重读账户与持仓，不信任任何早先缓存。
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

        // 执行价必须严格位于止损与止盈之间（多：stop < price < take；空：stop > price > take）。
        var isLong = request.Side == "long";
        if (isLong
            ? request.StopLossPrice >= executionPrice.Value || executionPrice.Value >= request.TakeProfitPrice!.Value
            : request.StopLossPrice <= executionPrice.Value || executionPrice.Value <= request.TakeProfitPrice!.Value)
            return Fail(ToolErrors.InvalidArgument, "执行价必须严格位于止损与止盈之间");

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

        // 合约规格读取放在重跑风控之前，仅作为风控输入；任何未知/缺失都在第 10 步拒绝，绝不提前放行。
        var spec = await _gateway.GetFuturesSpecAsync(shared, ct);
        var exchangeMaxLeverage = spec is null
            ? null
            : request.Side == "long" ? spec.MaxLongLeverage : spec.MaxShortLeverage;

        // 7) 用最新账户/持仓/价格重跑风控；批准边界之外再按重跑计划收紧数量/杠杆/保证金。
        var discipline = await _accounts.GetDisciplineAsync(ct);
        var riskContext = new OpenRiskContext(account.UsdtEquity, account.UsdtAvailable,
            margin.UsedMargin, positions.Positions.Count,
            positions.Positions.Any(position => TradingRiskPolicy.BaseAsset(position.Symbol)
                == TradingRiskPolicy.BaseAsset(request.Symbol)),
            executionPrice.Value, exchangeMaxLeverage is > 0 ? exchangeMaxLeverage.Value : maxLeverage,
            spec?.QuantityStep ?? 0m, spec?.MinTradeQuantity ?? 0m, spec?.MinNotionalValue ?? 0m, discipline);
        if (ProposalRiskAdapter.ValidateOpen(proposal, _config.RiskControl, riskContext, out var plan) is { } riskError)
            return Fail(ToolErrors.RiskPolicyViolation, riskError);
        if (plan is null) return Fail(ToolErrors.RiskPolicyViolation, "风控计划不可用，拒绝开仓");
        if (currentLeverage > plan.Leverage) return Fail(ToolErrors.LeverageExceeded, "杠杆超过风控计划");
        if (request.Quantity > plan.Quantity) return Fail(ToolErrors.QuantityExceeded, "数量超过风控计划");
        if (submittedMargin > plan.Margin) return Fail(ToolErrors.QuantityExceeded, "保证金超过风控计划");

        // 8) 行情新鲜度：执行标的采样不得超过 3 分钟；未来时间戳同样视为不可信。
        var fresh = await _market.GetSnapshotAsync(_exchange, shared, _ctx.Timeframe, 2, 0, ct);
        var now = DateTime.UtcNow;
        if (fresh.Source != "live" || fresh.AsOfUtc is not { } asOf
            || asOf > now || now - asOf > TimeSpan.FromMinutes(3))
            return Fail(ToolErrors.StaleMarketData, "行情缺失、时间戳异常或超过 3 分钟");

        // 9) 重复与上限：同一提案已成功提交则幂等返回；未知结果阻止同账户同标的所有新开仓；
        //    同标的 10 秒内/本周期同标的最多 1 笔；同一提案最多 2 次提交尝试（仅限明确未创建）。
        var existing = await _db.TraderOrders.AsNoTracking()
            .Where(o => o.CycleId == _ctx.CycleId && o.ProposalId == request.ProposalId
                && (o.Action == "open" || o.Action == "buy" || o.Action == "sell") && o.Success)
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return ExistingOrder(existing);

        var unresolved = await _db.TraderOrders.AsNoTracking()
            .Where(o => o.ExchangeAccountId == _ctx.Account.Id && o.Status == "unknown"
                && (o.Action == "open" || o.Action == "buy" || o.Action == "sell"))
            .ToListAsync(ct);
        if (unresolved.Any(order => SameBaseAsset(order.Symbol, request.Symbol)))
            return Fail(ToolErrors.ExchangeUnknown, "该账户标的存在结果未知的开仓，禁止再次开仓，请人工核对");

        var at = DateTime.UtcNow;
        var recent = await _db.TraderOrders.AsNoTracking()
            .Where(o => o.ExchangeAccountId == _ctx.Account.Id && o.At >= at.AddSeconds(-10))
            .ToListAsync(ct);
        if (recent.Any(order => IsEffectiveOpen(order) && SameBaseAsset(order.Symbol, request.Symbol)
                && !(_ctx.Mode == "live" && order.Simulated)))
            return Fail(ToolErrors.OrderLimitReached, "同一标的 10 秒内已提交开仓");

        var cycleOrders = await _db.TraderOrders.AsNoTracking()
            .Where(o => o.CycleId == _ctx.CycleId)
            .ToListAsync(ct);
        if (cycleOrders.Any(order => order.ProposalId == request.ProposalId && order.Status == "unknown"))
            return Fail(ToolErrors.ExchangeUnknown, "上一笔提交结果未知，禁止重试，请人工核对");
        if (cycleOrders.Any(order => IsEffectiveOpen(order) && SameBaseAsset(order.Symbol, request.Symbol)))
            return Fail(ToolErrors.OrderLimitReached, "本周期该标的已提交过开仓");

        // 同一提案最多 2 次提交尝试：第二次仅用于交易所明确未创建订单的失败。
        var submittedAttempts = cycleOrders.Count(order => order.ProposalId == request.ProposalId);
        if (submittedAttempts >= 2) return Fail(ToolErrors.OrderLimitReached, "同一提案最多提交 2 次");

        // 尝试序号按已落库尝试数推进，避免同一周期内重启工具实例后复用旧 clientOrderId。
        var attempt = Math.Max(_attempt, submittedAttempts);
        _attempt = attempt + 1;
        var clientOrderId = ClientOrderId(_ctx.CycleId, request.ProposalId, attempt);
        if (await _db.TraderOrders.AnyAsync(
                o => o.CycleId == _ctx.CycleId && o.ClientOrderId == clientOrderId, ct))
            return Fail(ToolErrors.DuplicateOrder, "重复订单");

        // 10) 交易所校验：规格、能力与全部约束必须已知且满足；任何未知都不提交。
        if (spec is null) return Fail(ToolErrors.ExchangeUnknown, "合约规格未知，无法确认交易所能力");
        if (!spec.Trading) return Fail(ToolErrors.ExchangeUnknown, "标的当前不可交易");
        if (exchangeMaxLeverage is not > 0) return Fail(ToolErrors.ExchangeUnknown, "交易所方向杠杆上限未知");
        if (currentLeverage > exchangeMaxLeverage.Value) return Fail(ToolErrors.LeverageExceeded, "杠杆超过交易所上限");
        if (spec.MaxTradeQuantity is not > 0) return Fail(ToolErrors.ExchangeUnknown, "交易所最大下单量未知");
        if (request.Quantity > spec.MaxTradeQuantity.Value)
            return Fail(ToolErrors.QuantityExceeded, "数量超过交易所上限");
        if (spec.QuantityStep is not > 0) return Fail(ToolErrors.ExchangeUnknown, "交易所数量步长未知");
        if (request.Quantity % spec.QuantityStep.Value != 0)
            return Fail(ToolErrors.InvalidArgument, "数量精度不符合交易所步长");
        if (spec.MinTradeQuantity is not > 0) return Fail(ToolErrors.ExchangeUnknown, "交易所最小下单量未知");
        if (request.Quantity < spec.MinTradeQuantity.Value)
            return Fail(ToolErrors.InvalidArgument, "数量低于交易所最小下单量");
        if (spec.MinNotionalValue is not > 0) return Fail(ToolErrors.ExchangeUnknown, "交易所最小名义价值未知");
        var notional = request.Quantity * executionPrice.Value;
        if (notional < spec.MinNotionalValue.Value)
            return Fail(ToolErrors.InvalidArgument, "名义价值低于交易所最小下单金额");
        if (spec.PriceStep is not > 0) return Fail(ToolErrors.ExchangeUnknown, "交易所价格步长未知");
        if (request.Price is { } orderPrice && orderPrice % spec.PriceStep.Value != 0)
            return Fail(ToolErrors.InvalidArgument, "价格精度不符合交易所步长");

        // 实盘能力：止损为硬性保护、止盈为硬性目标，任一未知/不支持都不提交；演练可模拟。
        if (_ctx.Mode == "live")
        {
            var capability = _gateway.Audit();
            if (!capability.CanTrade) return Fail(ToolErrors.ExchangeUnknown, "账户不具备实盘交易能力");
            if (!capability.StopLossSupported) return Fail(ToolErrors.ExchangeUnknown, "交易所不支持附加止损");
            if (!capability.TakeProfitSupported) return Fail(ToolErrors.ExchangeUnknown, "交易所不支持附加止盈");
        }

        // 11) 演练：使用新鲜最新价按配置滑点模拟成交，绝不调用下单接口。
        if (_ctx.Mode == "dry")
        {
            if (fresh.LastPrice is not > 0)
                return Fail(ToolErrors.DataUnavailable, "最新价不可用，无法模拟成交", true);
            var fillPrice = SimulatedFillPrice(request, fresh.LastPrice.Value);
            var fee = request.Quantity * fillPrice * _config.RiskControl.EstimatedFeePct / 100m;
            var note = $"{request.Reason}（演练成交：参考价 {fresh.LastPrice.Value}，滑点 {_config.RiskControl.EstimatedSlippagePct}%，手续费 {fee:0.########} USDT）";
            var simulated = NewOrder(clientOrderId, request, fillPrice, currentLeverage, "filled",
                simulated: true, request.Quantity, fillPrice, proposal.Id, verdict.Id, note);
            var (inserted, raced) = await TryInsertOrderAsync(simulated);
            if (!inserted)
                return raced is not null
                    ? ExistingOrder(raced, "已存在相同的 clientOrderId，未重复提交；如状态未知请人工核对")
                    : Fail(ToolErrors.DataUnavailable, "订单无法落库，未模拟成交", true);
            _hub.Publish(SseChannels.Trader(_ctx.Trader.Id), new SseEvent("order", simulated));
            return ToolEnvelope.Success(OrderData(simulated), DateTimeOffset.UtcNow, "live",
                ["演练模式：成交为模拟，不代表交易所真实成交"]);
        }

        // 11) 实盘：提交前先落 unknown 占位行（唯一索引兜底），提交开始后即使调用方取消/落库失败也保留记录。
        ct.ThrowIfCancellationRequested();
        var row = NewOrder(clientOrderId, request, executionPrice.Value, currentLeverage, "unknown",
            simulated: false, null, null, proposal.Id, verdict.Id, request.Reason);
        var (placeholderInserted, competing) = await TryInsertOrderAsync(row);
        if (!placeholderInserted)
            return competing is not null
                ? ExistingOrder(competing, "已存在相同的 clientOrderId，未重复提交；如状态未知请人工核对")
                : Fail(ToolErrors.DataUnavailable, "订单无法落库，未提交", true);

        PlaceResult placed;
        try
        {
            placed = await _gateway.PlaceAsync(shared, request.Side == "long", request.Quantity,
                request.StopLossPrice, request.TakeProfitPrice, currentLeverage, request.OrderType,
                request.OrderType == "limit" ? request.Price : null, clientOrderId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消：占位 unknown 行已落库，直接上抛；绝不删除唯一订单记录。
            throw;
        }
        catch (Exception ex)
        {
            // 网关抛异常（超时/网络/解析）无法确认订单是否创建，按未知处理。
            LogGatewayError(PlaceOutcome.Unknown, ex.Message);
            placed = PlaceResult.Unknown(ex.Message);
        }
        ct.ThrowIfCancellationRequested();

        // 12) 落库与推送：只有交易所明确未创建才写 failed；未知一律 unknown 且不消耗重试机会。
        if (placed.Outcome != PlaceOutcome.Created) LogGatewayError(placed.Outcome, placed.Error);
        ApplyPlaceResult(row, placed, request.Quantity);
        if (!await TryPersistOrderAsync(row))
            return Fail(ToolErrors.ExchangeUnknown, "已提交交易所，但本地订单状态写入失败，请人工核对");
        _hub.Publish(SseChannels.Trader(_ctx.Trader.Id), new SseEvent("order", row));

        return placed.Outcome switch
        {
            PlaceOutcome.Created => ToolEnvelope.Success(OrderData(row), DateTimeOffset.UtcNow, "live"),
            PlaceOutcome.NotCreated => Fail(ToolErrors.ExchangeError, ExchangeRejectedMessage),
            _ => Fail(ToolErrors.ExchangeUnknown, ExchangeUnknownMessage)
        };
    }

    /// <summary>演练成交价：一律以最新价为基准按配置滑点成交，不得直接使用限价充当成交价。</summary>
    private decimal SimulatedFillPrice(OrderRequest request, decimal latestPrice)
    {
        var slippage = _config.RiskControl.EstimatedSlippagePct / 100m;
        return request.Side == "long" ? latestPrice * (1m + slippage) : latestPrice * (1m - slippage);
    }

    private static string StatusFor(PlaceResult placed, decimal quantity) => placed.Outcome switch
    {
        PlaceOutcome.NotCreated => "failed",
        PlaceOutcome.Unknown => "unknown",
        _ when placed.FilledQuantity is null or <= 0 => "submitted",
        _ when placed.FilledQuantity.Value >= quantity => "filled",
        _ => "partial"
    };

    private static void ApplyPlaceResult(TraderOrder row, PlaceResult placed, decimal quantity)
    {
        row.Status = StatusFor(placed, quantity);
        row.FilledQuantity = placed.FilledQuantity;
        row.AveragePrice = placed.AveragePrice;
        row.ExchangeOrderId = placed.ExchangeOrderId;
        row.Success = row.Status is "submitted" or "filled" or "partial";
        row.Error = placed.Outcome switch
        {
            PlaceOutcome.NotCreated => ExchangeRejectedMessage,
            PlaceOutcome.Unknown => ExchangeUnknownMessage,
            _ => null
        };
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
        Error = status == "unknown" ? "提交已发起，结果待确认" : null,
        At = DateTime.UtcNow
    };

    /// <summary>插入唯一索引行；冲突时重读已有订单（绝不重复提交交易所）。</summary>
    private async Task<(bool Inserted, TraderOrder? Existing)> TryInsertOrderAsync(TraderOrder row)
    {
        try
        {
            _db.TraderOrders.Add(row);
            await _db.SaveChangesAsync(CancellationToken.None);
            return (true, null);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.Entry(row).State = EntityState.Detached;
            var existing = await _db.TraderOrders.AsNoTracking()
                .SingleOrDefaultAsync(o => o.CycleId == row.CycleId && o.ClientOrderId == row.ClientOrderId,
                    CancellationToken.None);
            return (false, existing);
        }
        catch
        {
            _db.Entry(row).State = EntityState.Detached;
            return (false, null);
        }
    }

    /// <summary>更新已落库订单；失败时回滚内存状态，数据库保留提交前的 unknown 占位行。</summary>
    private async Task<bool> TryPersistOrderAsync(TraderOrder row)
    {
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch
        {
            var entry = _db.Entry(row);
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is SqliteException { SqliteErrorCode: 19 }) return true;
        return false;
    }

    private static void LogGatewayError(PlaceOutcome outcome, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        Console.WriteLine($"[place_order] {outcome} 原始错误：{Redact(raw)}");
    }

    /// <summary>内部日志脱敏：截断并移除 URL / 键值/令牌文本，绝不进入工具回执或订单错误字段。</summary>
    private static string Redact(string raw)
    {
        var text = raw.Length <= MaxInternalErrorChars ? raw : raw[..MaxInternalErrorChars];
        text = Regex.Replace(text, @"https?://\S+", "[url]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?i)\b(api[-_ ]?key|secret|passphrase|token)\b\s*[:=]\s*\S+", "$1=[redacted]");
        text = Regex.Replace(text, @"\b[A-Za-z0-9+/_=-]{24,}\b", "[redacted]");
        return text;
    }

    private static ToolEnvelope ExistingOrder(TraderOrder order, string? warning = null) =>
        ToolEnvelope.Success(OrderData(order), DateTimeOffset.UtcNow, "live",
            [warning ?? "该提案已成功提交，返回已有订单"]);

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
        order.Action is "open" or "buy" or "sell" && (order.Success
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

    private static SemaphoreSlim[] CreateAccountLocks() =>
        Enumerable.Range(0, AccountLockStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private static SemaphoreSlim AccountLock(int accountId) =>
        AccountLocks[(int)((uint)accountId % AccountLockStripes)];

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max];

    private static ToolEnvelope Fail(string code, string message, bool retryable = false) =>
        ToolEnvelope.Failure(code, message, retryable);
}
