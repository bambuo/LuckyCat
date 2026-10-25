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
/// <paramref name="SseChannel"/> 是本周期事件的目标频道，由
/// <see cref="LuckyCat.Workflows.TradingCycleContext.SseChannel"/> 单点决策并经 <c>ForStage</c> 线程化
/// （R-11-7：试运行事件必须送达本周期频道，绝不硬编码到交易员频道）。
/// </summary>
public sealed record ExecutionToolContext(
    string CycleId, string StageRunId, string Mode, string Exchange, string Timeframe,
    Trader Trader, ExchangeAccount Account, StrategyConfig Config, ICycleBudget Budget, string SseChannel);

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
    private const string CloseRejectedMessage = "交易所拒绝平仓，订单未创建";
    private const string CloseUnknownMessage = "平仓结果未知，请人工核对";
    private const string CancelRejectedMessage = "交易所拒绝撤单，订单未变更";
    private const string CancelUnknownMessage = "撤单结果未知，请人工核对";
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

    [Description("平仓（整仓或部分、市价或限价）。风险降低操作，不要求风控裁决；手动平仓可省略提案。")]
    public async Task<ToolEnvelope> ClosePositionAsync(
        [Description("本周期平仓提案 ID；手动平仓省略")] string? proposalId,
        [Description("标的，必须等于当前持仓标的（如 BTC/USDT）")] string symbol,
        [Description("平仓数量，缺省为整仓，不得超过当前持仓")] decimal? quantity,
        [Description("订单类型：market | limit")] string orderType,
        [Description("限价单价格，与最新价偏差 ≤2%")] decimal? price,
        [Description("平仓理由，审计用，≤300 字")] string reason,
        CancellationToken ct = default)
    {
        var request = new CloseRequest(proposalId, symbol, quantity, orderType, price, reason);
        // 与开仓一致：账户锁覆盖校验、交易所调用、订单落库与 ToolInvocation 落库（无二次审计）。
        var accountLock = AccountLock(_ctx.Account.Id);
        await accountLock.WaitAsync(ct);
        try
        {
            return await _audit.RunAsync("close_position", Role, request, () => ClosePositionCoreAsync(request, ct), ct);
        }
        finally
        {
            accountLock.Release();
        }
    }

    [Description("撤销本交易员创建且仍处于 submitted/partial 的订单；不得撤销其他交易员/账户的订单。")]
    public async Task<ToolEnvelope> CancelOrderAsync(
        [Description("交易所订单号或客户端订单号")] string orderId,
        CancellationToken ct = default)
    {
        var accountLock = AccountLock(_ctx.Account.Id);
        await accountLock.WaitAsync(ct);
        try
        {
            return await _audit.RunAsync("cancel_order", Role, new { orderId },
                () => CancelOrderCoreAsync(orderId, ct), ct);
        }
        finally
        {
            accountLock.Release();
        }
    }

    /// <summary>一次 close_position 的不可变入参（也作为 ToolInvocation 的审计入参）。</summary>
    private sealed record CloseRequest(string? ProposalId, string Symbol, decimal? Quantity,
        string OrderType, decimal? Price, string Reason);

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
            _hub.Publish(_ctx.SseChannel, new SseEvent("order", simulated));
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
        _hub.Publish(_ctx.SseChannel, new SseEvent("order", row));

        return placed.Outcome switch
        {
            PlaceOutcome.Created => ToolEnvelope.Success(OrderData(row), DateTimeOffset.UtcNow, "live"),
            PlaceOutcome.NotCreated => Fail(ToolErrors.ExchangeError, ExchangeRejectedMessage),
            _ => Fail(ToolErrors.ExchangeUnknown, ExchangeUnknownMessage)
        };
    }

    /// <summary>
    /// close_position 硬闸：持仓存在、数量 ≤ 持仓、可选提案归属、限价偏离 ≤2%、行情 ≤3 分钟、重复校验。
    /// 不要求风控裁决、不因「禁止开仓」拒绝；演练只模拟成交，实盘只调用网关一次并落库交易所订单号。
    /// </summary>
    private async Task<ToolEnvelope> ClosePositionCoreAsync(CloseRequest request, CancellationToken ct)
    {
        // 1) 参数、模式与周期归属（fail-closed）。
        if (_ctx.Mode is not ("dry" or "live")) return Fail(ToolErrors.InvalidArgument, "周期执行模式无效");
        if (!await CurrentCycleOwnedAsync(ct)) return Fail(ToolErrors.InvalidArgument, "周期归属不匹配");
        if (string.IsNullOrWhiteSpace(request.Symbol)) return Fail(ToolErrors.InvalidArgument, "symbol 必填");
        if (request.OrderType is not ("market" or "limit")) return Fail(ToolErrors.InvalidArgument, "orderType 无效");
        if (request.OrderType == "limit" && request.Price is not > 0)
            return Fail(ToolErrors.InvalidArgument, "限价单必须提供有效价格");
        if (request.Price is <= 0) return Fail(ToolErrors.InvalidArgument, "价格必须大于 0");
        if (string.IsNullOrWhiteSpace(request.Reason)) return Fail(ToolErrors.InvalidArgument, "reason 必填");
        if (request.Reason.Length > 300) return Fail(ToolErrors.InvalidArgument, "reason 不得超过 300 字");

        // 2) 持仓必须存在；平仓是风险降低操作，不读取裁决。
        var positions = await _accounts.GetPositionsAsync(ct);
        if (!positions.Success) return Fail(ToolErrors.DataUnavailable, "持仓不可读", true);
        var position = positions.Positions.FirstOrDefault(p =>
            TradingRiskPolicy.BaseAsset(p.Symbol) == TradingRiskPolicy.BaseAsset(request.Symbol));
        if (position is null) return Fail(ToolErrors.PositionNotFound, "无该标的持仓");

        var positionQuantity = Math.Abs(position.Quantity);
        var qty = request.Quantity ?? positionQuantity;
        if (qty <= 0 || qty > positionQuantity) return Fail(ToolErrors.QuantityExceeded, "平仓数量超过持仓");

        // 3) 可选提案必须属于本周期且 action=close（手动平仓省略）。
        if (request.ProposalId is not null)
        {
            var closeProposal = await _db.TradeProposals.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == request.ProposalId, ct);
            if (closeProposal is null || closeProposal.CycleId != _ctx.CycleId
                || closeProposal.Action != "close"
                || (closeProposal.TraderId is { } proposalTraderId && proposalTraderId != _ctx.Trader.Id)
                || (closeProposal.Symbol is { } proposalSymbol && !SameBaseAsset(proposalSymbol, request.Symbol)))
                return Fail(ToolErrors.InvalidArgument, "平仓提案不存在或不属于本周期");
        }

        // 4) 最新价与限价偏离（≤2%）。
        var shared = CoinRankingService.ParseConfiguredSymbol(request.Symbol);
        var latest = await _gateway.GetPriceAsync(shared, ct);
        if (latest is not > 0) return Fail(ToolErrors.DataUnavailable, "最新价不可用", true);
        if (request.OrderType == "limit" && Math.Abs(request.Price!.Value / latest.Value - 1m) * 100m > 2m)
            return Fail(ToolErrors.PriceOutOfBand, "限价偏离最新价超过 2%");
        var execPrice = request.OrderType == "limit" ? request.Price!.Value : latest.Value;

        // 5) 行情新鲜度：与开仓同口径，缺失/未来时间戳/超过 3 分钟一律拒绝。
        var fresh = await _market.GetSnapshotAsync(_exchange, shared, _ctx.Timeframe, 2, 0, ct);
        var now = DateTime.UtcNow;
        if (fresh.Source != "live" || fresh.AsOfUtc is not { } asOf
            || asOf > now || now - asOf > TimeSpan.FromMinutes(3))
            return Fail(ToolErrors.StaleMarketData, "行情缺失、时间戳异常或超过 3 分钟");

        // 6) 幂等键与重复校验（沿用开仓的生成规则与唯一索引）；同一平仓提案已提交则返回已有订单。
        if (request.ProposalId is not null)
        {
            var existingClose = await _db.TraderOrders.AsNoTracking()
                .Where(o => o.CycleId == _ctx.CycleId && o.ProposalId == request.ProposalId
                    && o.Action == "close"
                    && (o.Status == "submitted" || o.Status == "partial" || o.Status == "filled"))
                .OrderByDescending(o => o.Id)
                .FirstOrDefaultAsync(ct);
            if (existingClose is not null) return ExistingOrder(existingClose);
        }

        // 结果未知的平仓可能已成交：同账户同标的一律禁止再次平仓，避免重复减仓甚至反手。
        var unresolvedCloses = await _db.TraderOrders.AsNoTracking()
            .Where(o => o.ExchangeAccountId == _ctx.Account.Id && o.Action == "close" && o.Status == "unknown")
            .ToListAsync(ct);
        if (unresolvedCloses.Any(o => SameBaseAsset(o.Symbol, request.Symbol)))
            return Fail(ToolErrors.ExchangeUnknown, "该账户标的存在结果未知的平仓，禁止再次平仓，请人工核对");

        var priorCloses = await _db.TraderOrders.AsNoTracking()
            .CountAsync(o => o.CycleId == _ctx.CycleId && o.Action == "close", ct);
        var attempt = Math.Max(_attempt, priorCloses);
        _attempt = attempt + 1;
        var clientOrderId = ClientOrderId(_ctx.CycleId, request.ProposalId ?? $"close-{request.Symbol}", attempt);
        if (await _db.TraderOrders.AnyAsync(
                o => o.CycleId == _ctx.CycleId && o.ClientOrderId == clientOrderId, ct))
            return Fail(ToolErrors.DuplicateOrder, "重复订单");

        var isLong = position.Side == SharedPositionSide.Long;
        var simulated = _ctx.Mode == "dry";
        var row = new TraderOrder
        {
            CycleId = _ctx.CycleId,
            TraderId = _ctx.Trader.Id,
            ExchangeAccountId = _ctx.Account.Id,
            ProposalId = request.ProposalId,
            VerdictId = null,
            ClientOrderId = clientOrderId,
            Action = "close",
            Side = isLong ? "long" : "short",
            Symbol = request.Symbol,
            Quantity = qty,
            Price = execPrice,
            Leverage = position.Leverage,
            Simulated = simulated,
            Status = simulated ? "filled" : "unknown",
            // 成交字段只允许来自模拟成交或交易所确认回报；提交数量/参考价不是成交。
            FilledQuantity = null,
            AveragePrice = null,
            Success = false,
            Note = Truncate(request.Reason, 500),
            Error = simulated ? null : CloseUnknownMessage,
            At = DateTime.UtcNow
        };

        // 7) 演练：不调用交易所，按最新价±配置滑点模拟成交（限价单不得越过限价）。
        if (simulated)
        {
            if (fresh.LastPrice is not > 0)
                return Fail(ToolErrors.DataUnavailable, "最新价不可用，无法模拟成交", true);
            var simulatedFill = SimulatedCloseFillPrice(isLong, fresh.LastPrice.Value,
                request.OrderType == "limit" ? request.Price : null);
            row.FilledQuantity = qty;
            row.AveragePrice = simulatedFill;
            row.Success = true;
            row.Error = null;
            var (inserted, raced) = await TryInsertOrderAsync(row);
            if (!inserted)
                return raced is not null
                    ? ExistingOrder(raced, "已存在相同的 clientOrderId，未重复提交；如状态未知请人工核对")
                    : Fail(ToolErrors.DataUnavailable, "订单无法落库，未模拟成交", true);
            _hub.Publish(_ctx.SseChannel, new SseEvent("order", row));
            return ToolEnvelope.Success(OrderData(row), DateTimeOffset.UtcNow, "live",
                ["演练模式：平仓为模拟，不代表交易所真实成交"]);
        }

        // 8) 实盘：先落 unknown 占位行（唯一索引兜底），再调用网关一次。
        ct.ThrowIfCancellationRequested();
        var (placeholderInserted, competing) = await TryInsertOrderAsync(row);
        if (!placeholderInserted)
            return competing is not null
                ? ExistingOrder(competing, "已存在相同的 clientOrderId，未重复提交；如状态未知请人工核对")
                : Fail(ToolErrors.DataUnavailable, "订单无法落库，未提交", true);

        CloseResult closed;
        try
        {
            closed = await _gateway.CloseAsync(shared, qty, isLong, request.OrderType,
                request.OrderType == "limit" ? request.Price : null, clientOrderId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消：占位 unknown 行已落库，直接上抛；绝不删除唯一订单记录。
            throw;
        }
        catch (Exception ex)
        {
            // 网关抛异常（超时/网络/解析）无法确认是否已平仓：占位行保持 unknown，禁止按失败重试。
            LogGatewayError("close_position", ex.Message);
            _hub.Publish(_ctx.SseChannel, new SseEvent("order", row));
            return Fail(ToolErrors.ExchangeUnknown, CloseUnknownMessage);
        }
        ct.ThrowIfCancellationRequested();

        switch (closed.Outcome)
        {
            case CloseOutcome.Unknown:
                // 未知 = 订单可能已创建/成交：保持 unknown，禁止重试或补单。
                LogGatewayError("close_position", closed.Error);
                row.Status = "unknown";
                row.Success = false;
                row.FilledQuantity = null;
                row.AveragePrice = null;
                row.Error = CloseUnknownMessage;
                if (!await TryPersistOrderAsync(row))
                    return Fail(ToolErrors.ExchangeUnknown, "已提交交易所，但本地订单状态写入失败，请人工核对");
                _hub.Publish(_ctx.SseChannel, new SseEvent("order", row));
                return Fail(ToolErrors.ExchangeUnknown, CloseUnknownMessage);

            case CloseOutcome.Rejected:
                // 交易所明确拒绝：确定未创建，可安全记为 failed。
                LogGatewayError("close_position", closed.Error);
                row.Status = "failed";
                row.Success = false;
                row.FilledQuantity = null;
                row.AveragePrice = null;
                row.ExchangeOrderId = null;
                row.Error = CloseRejectedMessage;
                if (!await TryPersistOrderAsync(row))
                    return Fail(ToolErrors.ExchangeUnknown, "已提交交易所，但本地订单状态写入失败，请人工核对");
                _hub.Publish(_ctx.SseChannel, new SseEvent("order", row));
                return Fail(ToolErrors.ExchangeError, CloseRejectedMessage);
        }

        // 交易所已受理：成交未经回报确认，一律记为 submitted；成交字段保持 null。
        row.Status = "submitted";
        row.Success = true;
        row.FilledQuantity = null;
        row.AveragePrice = null;
        row.ExchangeOrderId = closed.ExchangeOrderId;
        row.Error = null;

        // 限价平仓若没有交易所订单号，将无法撤单/对账：按未知处理，绝不静默返回可撤的成功。
        if (request.OrderType == "limit" && closed.ExchangeOrderId is null)
        {
            row.Status = "unknown";
            row.Success = false;
            row.Error = "限价平仓已受理但未返回交易所订单号，无法撤单";
            if (!await TryPersistOrderAsync(row))
                return Fail(ToolErrors.ExchangeUnknown, "已提交交易所，但本地订单状态写入失败，请人工核对");
            _hub.Publish(_ctx.SseChannel, new SseEvent("order", row));
            return Fail(ToolErrors.ExchangeUnknown, "限价平仓已受理但未返回交易所订单号，无法撤单，请人工核对");
        }

        if (!await TryPersistOrderAsync(row))
            return Fail(ToolErrors.ExchangeUnknown, "已提交交易所，但本地订单状态写入失败，请人工核对");
        _hub.Publish(_ctx.SseChannel, new SseEvent("order", row));
        return ToolEnvelope.Success(OrderData(row), DateTimeOffset.UtcNow, "live");
    }

    /// <summary>
    /// cancel_order：只撤销本交易员、当前账户、仍处于 submitted/partial 的订单。
    /// 演练只撤演练订单、实盘只撤实盘订单；实盘必须携带交易所订单号且只调用一次，
    /// 结果未知时保持订单原状态，绝不标记成功。
    /// </summary>
    private async Task<ToolEnvelope> CancelOrderCoreAsync(string orderId, CancellationToken ct)
    {
        if (_ctx.Mode is not ("dry" or "live")) return Fail(ToolErrors.InvalidArgument, "周期执行模式无效");
        if (!await CurrentCycleOwnedAsync(ct)) return Fail(ToolErrors.InvalidArgument, "周期归属不匹配");
        if (string.IsNullOrWhiteSpace(orderId)) return Fail(ToolErrors.InvalidArgument, "orderId 必填");

        // 归属校验：只认本交易员的订单；账户快照存在时还必须等于当前账户。
        var order = await _db.TraderOrders
            .Where(o => o.TraderId == _ctx.Trader.Id
                && (o.ExchangeOrderId == orderId || o.ClientOrderId == orderId))
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync(ct);
        if (order is null) return Fail(ToolErrors.OrderNotCancellable, "订单不存在");
        if (order.Status is not ("submitted" or "partial"))
            return Fail(ToolErrors.OrderNotCancellable, "订单状态不可撤");
        if (order.ExchangeAccountId is { } orderAccountId && orderAccountId != _ctx.Account.Id)
            return Fail(ToolErrors.OrderNotCancellable, "订单不属于当前账户");

        // 订单周期归属：存在周期快照时，归属与演练标记必须一致（历史行可为空）。
        if (order.CycleId is { } orderCycleId)
        {
            var orderCycle = await _db.TradingCycles.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == orderCycleId, ct);
            if (orderCycle is null || orderCycle.TraderId != _ctx.Trader.Id
                || orderCycle.ExchangeAccountId != _ctx.Account.Id)
                return Fail(ToolErrors.OrderNotCancellable, "订单周期归属不匹配");
            if ((orderCycle.Mode == "dry") != order.Simulated)
                return Fail(ToolErrors.OrderNotCancellable, "订单演练标记与周期模式不一致");
        }

        // 演练/实盘隔离：演练绝不能改写实盘订单，实盘也不能碰演练订单。
        if (_ctx.Mode == "dry" && !order.Simulated)
            return Fail(ToolErrors.OrderNotCancellable, "演练模式只能撤销演练订单");
        if (_ctx.Mode == "live" && order.Simulated)
            return Fail(ToolErrors.OrderNotCancellable, "实盘模式不能撤销演练订单");

        if (_ctx.Mode != "dry")
        {
            if (order.Symbol is null && order.ExchangeOrderId is null)
                return Fail(ToolErrors.OrderNotCancellable, "订单缺少标的与交易所订单号，无法撤单");
            if (order.Symbol is null)
                return Fail(ToolErrors.OrderNotCancellable, "订单缺少标的，无法撤单");
            if (order.ExchangeOrderId is null)
                return Fail(ToolErrors.OrderNotCancellable, "订单缺少交易所订单号，无法撤单");
            var shared = CoinRankingService.ParseConfiguredSymbol(order.Symbol);
            CancelResult canceled;
            try
            {
                canceled = await _gateway.CancelAsync(shared, order.ExchangeOrderId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 结果未知：订单保持 submitted，绝不标记 cancelled。
                LogGatewayError("cancel_order", ex.Message);
                return Fail(ToolErrors.ExchangeUnknown, CancelUnknownMessage);
            }
            switch (canceled.Outcome)
            {
                case CancelOutcome.Unknown:
                    LogGatewayError("cancel_order", canceled.Error);
                    return Fail(ToolErrors.ExchangeUnknown, CancelUnknownMessage);
                case CancelOutcome.Rejected:
                    LogGatewayError("cancel_order", canceled.Error);
                    return Fail(ToolErrors.ExchangeError, CancelRejectedMessage);
            }
        }

        order.Status = "cancelled";
        order.Success = false;
        if (!await TryPersistOrderAsync(order))
            return Fail(ToolErrors.ExchangeUnknown, "撤单结果无法落库，请人工核对");
        _hub.Publish(_ctx.SseChannel, new SseEvent("order", order));
        return ToolEnvelope.Success(OrderData(order), DateTimeOffset.UtcNow, "live");
    }

    /// <summary>当前周期必须属于本交易员、本策略、本账户且模式一致（与 place_order 第 2 步同口径）。</summary>
    private async Task<bool> CurrentCycleOwnedAsync(CancellationToken ct)
    {
        var cycle = await _db.TradingCycles.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == _ctx.CycleId, ct);
        return cycle is not null && cycle.TraderId == _ctx.Trader.Id
            && cycle.StrategyId == _ctx.Trader.StrategyId
            && cycle.ExchangeAccountId == _ctx.Account.Id
            && cycle.Mode == _ctx.Mode;
    }

    /// <summary>演练成交价：一律以最新价为基准按配置滑点成交，不得直接使用限价充当成交价。</summary>
    private decimal SimulatedFillPrice(OrderRequest request, decimal latestPrice)
    {
        var slippage = _config.RiskControl.EstimatedSlippagePct / 100m;
        return request.Side == "long" ? latestPrice * (1m + slippage) : latestPrice * (1m - slippage);
    }

    /// <summary>
    /// 演练平仓价：平多按最新价减滑点（卖出），平空按最新价加滑点（买入）；
    /// 限价单不得越过限价（卖出不低于限价、买入不高于限价），因此按限价截断。
    /// </summary>
    private decimal SimulatedCloseFillPrice(bool isLong, decimal latestPrice, decimal? limitPrice)
    {
        var slippage = _config.RiskControl.EstimatedSlippagePct / 100m;
        var simulated = isLong ? latestPrice * (1m - slippage) : latestPrice * (1m + slippage);
        return limitPrice is not { } limit
            ? simulated
            : isLong ? Math.Max(simulated, limit) : Math.Min(simulated, limit);
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

    private static void LogGatewayError(string tool, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        Console.WriteLine($"[{tool}] 原始错误：{Redact(raw)}");
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
