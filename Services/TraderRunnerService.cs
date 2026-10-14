using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>
/// 交易员后台执行器：每个交易员一个独立循环——按扫描间隔运行 AI 测试（真实行情 + 绑定策略），
/// 决策落库（可追溯），读取真实账户净值采样，并按「实盘/演练」执行（智能交易；网格暂不自动执行）。
/// 启动即执行首轮；应用重启自动恢复运行中的交易员；依赖缺失自动停止并落库状态。
/// </summary>
public class TraderRunnerService(IServiceScopeFactory scopeFactory, PositionHistorySyncService equitySync, SseEventHub hub) : BackgroundService
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _loops = new();
    // 同账户多个交易员的开仓检查与提交串行，避免同时绕过最大持仓/资金占用限制。
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ExecutionLocks = new();

    public bool IsRunning(int traderId) => _loops.ContainsKey(traderId);

    public void Start(int traderId)
    {
        if (_loops.ContainsKey(traderId)) return;
        var cts = new CancellationTokenSource();
        if (_loops.TryAdd(traderId, cts))
        {
            _ = Task.Run(() => RunLoopAsync(traderId, cts.Token));
        }
        else
        {
            cts.Dispose();
        }
    }

    public void Stop(int traderId)
    {
        if (_loops.TryRemove(traderId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>编辑运行中的交易员后调用：以新配置重启循环。</summary>
    public void Restart(int traderId)
    {
        if (!_loops.ContainsKey(traderId)) return;
        Stop(traderId);
        Start(traderId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 应用启动时恢复运行中的交易员（首轮立即执行）
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = await db.Traders.Where(t => t.IsRunning).Select(t => t.Id).ToListAsync(stoppingToken);
            foreach (var id in ids)
            {
                Console.WriteLine($"[Trader] 恢复运行中的交易员 #{id}");
                Start(id);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trader] 恢复交易员失败：{ex.Message}");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 应用退出：停止全部循环
        }

        foreach (var id in _loops.Keys.ToList())
        {
            Stop(id);
        }
    }

    private async Task RunLoopAsync(int traderId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var intervalMinutes = 3;
            try
            {
                using var scope = scopeFactory.CreateScope();
                intervalMinutes = await RunCycleAsync(scope, traderId, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Trader] #{traderId} 轮次异常：{ex.GetType().Name} {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 3, 60)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _loops.TryRemove(traderId, out _);
    }

    private async Task<int> RunCycleAsync(IServiceScope scope, int traderId, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var cycleActivity = Telemetry.Source.StartActivity("trader.cycle");
        cycleActivity?.SetTag("trader.id", traderId);
        var trader = await db.Traders.FindAsync([traderId], ct);
        if (trader is null || !trader.IsRunning)
        {
            Stop(traderId);
            return 3;
        }

        var provider = await db.Providers.FindAsync([trader.ProviderId], ct);
        var account = await db.ExchangeAccounts.FindAsync([trader.ExchangeAccountId], ct);
        var strategy = await db.Strategies.FindAsync([trader.StrategyId], ct);
        if (provider is null || account is null || strategy is null)
        {
            Console.WriteLine($"[Trader] #{traderId} 绑定对象缺失（提供商/交易所/策略已删除），自动停止");
            trader.IsRunning = false;
            trader.StartedAt = null;
            trader.LastCycleStatus = "skipped";
            await db.SaveChangesAsync(ct);
            hub.Publish(SseChannels.Trader(traderId), new SseEvent("cycle", trader));
            Stop(traderId);
            return trader.ScanIntervalMinutes;
        }

        var config = JsonSerializer.Deserialize<StrategyConfig>(strategy.ConfigJson, VerificationJson.Options) ?? new StrategyConfig();
        var runner = scope.ServiceProvider.GetRequiredService<StrategyTestRunner>();
        var memory = scope.ServiceProvider.GetRequiredService<DecisionMemoryService>();
        var trading = scope.ServiceProvider.GetRequiredService<ExchangeTradingService>();

        // 1) AI 测试（真实行情 + 历史决策记忆注入按策略既有口径；附账户摘要、当前持仓（含止损/止盈）与最近平仓——
        //    「持仓检查」、最小开仓金额折算与连亏减半/停手规则的数据底座；读取失败按空处理，不阻断循环）
        var snapshot = await trading.GetAccountAsync(account, ct);
        var positionRead = await trading.GetPositionSnapshotAsync(account, ct);
        var livePositions = positionRead.Positions;
        var openOrders = await db.TraderOrders
            .Where(o => o.TraderId == trader.Id && o.Success && (o.Action == "buy" || o.Action == "sell"))
            .OrderByDescending(o => o.Id)
            .Take(50)
            .Select(o => new { o.Symbol, o.StopLossPrice, o.TakeProfitPrice })
            .ToListAsync(ct);
        var stopsByBase = openOrders
            .Where(o => !string.IsNullOrWhiteSpace(o.Symbol))
            .GroupBy(o => BaseAssetOf(o.Symbol!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var positions = livePositions
            .Select(p =>
            {
                stopsByBase.TryGetValue(BaseAssetOf(p.Symbol), out var stops);
                var side = p.Side == CryptoExchange.Net.SharedApis.SharedPositionSide.Long ? "多头" : "空头";
                return new PromptPosition(p.Symbol, side, p.Quantity, p.AverageOpenPrice, p.UnrealizedPnl, p.Leverage,
                    stops?.StopLossPrice, stops?.TakeProfitPrice);
            })
            .ToList();
        var historyError = await equitySync.SyncAccountAsync(db, account, ct);
        var dayStart = TradingRiskPolicy.EvaluateDiscipline([], DateTime.UtcNow, TimeZoneInfo.Local).DayStartUtc;
        var recentCloses = await db.PositionHistories
            .Where(p => p.ExchangeAccountId == account.Id && p.CloseTime >= dayStart)
            .OrderByDescending(p => p.CloseTime).ToListAsync(ct);
        var fingerprint = PromptComposer.MemoryFingerprint(config);
        var marker = $"\"memoryFingerprint\":\"{fingerprint}\"";
        var currentRounds = db.DecisionRecords.Where(r => r.TraderId == trader.Id && r.StrategyId == trader.StrategyId && r.DecisionJson.Contains(marker));
        var lastNonHold = await currentRounds.Where(r => r.Action != "hold" && r.Action != "error")
            .Select(r => (int?)r.Id).MaxAsync(ct) ?? 0;
        var holds = await currentRounds.CountAsync(r => r.Id > lastNonHold && r.Action == "hold", ct);
        var discipline = TradingRiskPolicy.EvaluateDiscipline(recentCloses, DateTime.UtcNow, TimeZoneInfo.Local, holds);
        if (historyError is not null) discipline = discipline with { FeesKnown = false };
        var accountBrief = snapshot.Success ? new PromptAccount(snapshot.UsdtEquity, snapshot.UsdtAvailable,
            account.ExchangeType.ToString(), DateTime.UtcNow, UsedMargin(snapshot, livePositions)) : null;
        var history = await memory.LoadRecentAsync(trader.StrategyId, ct, trader.Id, fingerprint);
        var result = await runner.RunAsync(provider, config, history, ct, positionRead.Success ? positions : null, accountBrief, recentCloses, discipline);

        // 2) 决策落库（含思维链 / token 用量 / 耗时 / 提示词原文；TraderId 归属）
        await memory.RecordRoundAsync(trader.StrategyId, trader.Id, provider, result, fingerprint, ct);

        // 3) 真实净值采样（账户级，与看板口径一致：钱包 + 未实现；读取失败仅跳过，不阻断循环；快照已在步骤 1 读取）
        await equitySync.SampleEquityAsync(db, account, ct);

        // 3.5) 持仓采样（历史仓位杠杆回填用；仅变化时落库）
        await trading.CaptureSnapshotsAsync(db, account, ct);

        // 4) 执行（实盘/演练；仅智能交易）
        var executionLock = ExecutionLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        List<TraderOrder> newOrders;
        await executionLock.WaitAsync(ct);
        try
        {
            await ExecuteAsync(db, trader, account, config, result, snapshot, trading, ct);
            newOrders = db.ChangeTracker.Entries<TraderOrder>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
            await db.SaveChangesAsync(ct);
        }
        finally { executionLock.Release(); }

        trader.LastCycleAt = DateTime.UtcNow;
        trader.LastCycleStatus = result.Error is null ? "ok" : "error";

        // 本周期新增的审计行（开仓/平仓/跳过等）随状态一并落库，落库后按事件推送（看板不再轮询）
        await db.SaveChangesAsync(ct);
        foreach (var order in newOrders)
        {
            hub.Publish(SseChannels.Trader(trader.Id), new SseEvent("order", order));
        }
        hub.Publish(SseChannels.Trader(trader.Id), new SseEvent("cycle", trader));

        // 观测指标：决策计数 / AI 耗时 / token 用量（与周期 span 标签同步）
        var decisionAction = result.Decision is { } decisionJson ? ReadString(decisionJson, "action") ?? "unknown" : "none";
        Telemetry.Decisions.Add(1, new KeyValuePair<string, object?>("action", decisionAction));
        Telemetry.AiCallDuration.Record(result.ElapsedMs, new KeyValuePair<string, object?>("provider", provider.Name));
        if (result.InputTokens is { } inputTokens) Telemetry.AiTokens.Add(inputTokens, new KeyValuePair<string, object?>("type", "input"));
        if (result.OutputTokens is { } outputTokens) Telemetry.AiTokens.Add(outputTokens, new KeyValuePair<string, object?>("type", "output"));
        cycleActivity?.SetTag("trader.action", decisionAction);
        cycleActivity?.SetTag("trader.status", trader.LastCycleStatus);
        return trader.ScanIntervalMinutes;
    }

    internal async Task ExecuteAsync(
        AppDbContext db,
        Trader trader,
        ExchangeAccount account,
        StrategyConfig config,
        StrategyTestResultDto result,
        AccountSnapshot snapshot,
        ExchangeTradingService trading,
        CancellationToken ct)
    {
        if (config.StrategyType != "ai_trading")
        {
            Audit(db, trader.Id, null, "skip", null, null, true, "网格策略暂不支持自动执行", null);
            return;
        }

        if (result.Decision is not { } decision)
        {
            Audit(db, trader.Id, null, "skip", null, null, false, "本轮决策失败", result.Error ?? "未解析出决策");
            return;
        }

        var invalidDecision = TradingRiskPolicy.ValidateDecision(decision);
        if (result.Error is not null || invalidDecision is not null)
        {
            Audit(db, trader.Id, null, "skip", null, null, false, "决策契约校验失败", result.Error ?? invalidDecision);
            return;
        }
        var action = ReadString(decision, "action");
        var symbolText = ReadString(decision, "symbol");
        if (action is null || action == "hold" || string.IsNullOrWhiteSpace(symbolText))
        {
            return; // 观望/无标的：静默（不产生审计噪音）
        }

        var symbol = CoinRankingService.ParseConfiguredSymbol(symbolText);
        if (action == "close")
        {
            await ExecuteCloseAsync(db, trader, account, symbol, trading, ct);
            return;
        }

        if (action is "buy" or "sell")
        {
            var targetAsOf = result.SymbolMarketAsOf?.GetValueOrDefault(symbolText!) ?? result.MarketAsOf;
            if (result.ExecutableSymbols?.Contains(symbolText!, StringComparer.Ordinal) != true ||
                targetAsOf is not { } asOf || DateTime.UtcNow - asOf > TimeSpan.FromMinutes(3))
            {
                Audit(db, trader.Id, symbolText, action, null, null, false, "目标标的行情缺失、已裁剪或过期，禁止开仓", null);
                return;
            }
            var signal = result.SignalAssessments?.GetValueOrDefault(symbolText!);
            var sideSignal = action == "buy" ? signal?.Long : signal?.Short;
            if (PromptComposer.UsesDefaultEntry(config) && sideSignal?.Ready != true)
            {
                Audit(db, trader.Id, symbolText, action, null, null, false, "默认入场判据未齐备，拒绝开仓", null);
                return;
            }
            await ExecuteOpenAsync(db, trader, account, config, decision, symbol, action == "buy", snapshot, trading, ct);
        }
    }

    // 开仓：能力门（实盘）→ 已有持仓与重复加仓保护 → 止损必填 → 定量/最小额校验 → 设杠杆 → 市价下单（带止损/止盈）
    private async Task ExecuteOpenAsync(
        AppDbContext db,
        Trader trader,
        ExchangeAccount account,
        StrategyConfig config,
        JsonElement decision,
        CryptoExchange.Net.SharedApis.SharedSymbol symbol,
        bool isLong,
        AccountSnapshot snapshot,
        ExchangeTradingService trading,
        CancellationToken ct)
    {
        var actionText = isLong ? "buy" : "sell";
        if (trader.ExecutionMode != "live")
        {
            Audit(db, trader.Id, symbol.ToString(), actionText, null, null, true, "演练模式：未下单", null);
            Console.WriteLine($"[Trader] #{trader.Id} 演练模式跳过下单：{actionText} {symbol}");
            return;
        }

        var audit = ExchangeTradingService.Audit(account);
        if (!audit.CanTrade)
        {
            var reason = string.Join("；", audit.Notes);
            Audit(db, trader.Id, symbol.ToString(), actionText, null, null, false, "实盘能力不满足", reason);
            Console.WriteLine($"[Trader] #{trader.Id} 拒绝开仓：{reason}");
            return;
        }

        if (!snapshot.Success)
        {
            Audit(db, trader.Id, symbol.ToString(), actionText, null, null, false, "净值不可读，无法定量", snapshot.Error);
            return;
        }

        // 模型调用可能耗时数十秒：提交前重新读取账户与持仓，不信任早先缓存。
        snapshot = await trading.GetFreshAccountAsync(account, ct);
        var freshPositions = await trading.GetPositionSnapshotAsync(account, ct);
        if (!snapshot.Success || !freshPositions.Success)
        {
            Audit(db, trader.Id, symbol.ToString(), actionText, null, null, false, "账户或持仓读取失败，拒绝开仓", snapshot.Error ?? freshPositions.Error);
            return;
        }
        var pendingSince = DateTime.UtcNow.AddSeconds(-10);
        var recentOpen = await db.TraderOrders.AnyAsync(o => o.Success && (o.Action == "buy" || o.Action == "sell") && o.At >= pendingSince &&
            db.Traders.Any(t => t.Id == o.TraderId && t.ExchangeAccountId == account.Id), ct);
        if (recentOpen)
        {
            Audit(db, trader.Id, symbol.ToString(), actionText, null, null, false, "账户刚提交开仓，等待交易所持仓同步后再评估", null);
            return;
        }
        var price = await ExchangeTradingService.GetPriceAsync(account, symbol, ct);
        var futuresSymbol = await ExchangeTradingService.GetFuturesSymbolAsync(account, symbol, ct);
        if (price is not > 0 || futuresSymbol is null)
        {
            Audit(db, trader.Id, symbol.ToString(), actionText, null, null, false, "执行行情或合约规格不可用", null);
            return;
        }
        var syncError = await equitySync.SyncAccountAsync(db, account, ct);
        if (syncError is not null)
        {
            Audit(db, trader.Id, symbol.ToString(), actionText, null, price, false, "平仓历史同步失败，无法复核连亏纪律", syncError);
            return;
        }
        var start = TradingRiskPolicy.EvaluateDiscipline([], DateTime.UtcNow, TimeZoneInfo.Local).DayStartUtc;
        var closes = await db.PositionHistories.Where(p => p.ExchangeAccountId == account.Id && p.CloseTime >= start).ToListAsync(ct);
        var discipline = TradingRiskPolicy.EvaluateDiscipline(closes, DateTime.UtcNow, TimeZoneInfo.Local);
        var context = new OpenRiskContext(snapshot.UsdtEquity, snapshot.UsdtAvailable,
            UsedMargin(snapshot, freshPositions.Positions), freshPositions.Positions.Count,
            freshPositions.Positions.Any(p => TradingRiskPolicy.BaseAsset(p.Symbol) == symbol.BaseAsset),
            price.Value, (isLong ? futuresSymbol.MaxLongLeverage : futuresSymbol.MaxShortLeverage) ?? 1m,
            futuresSymbol.QuantityStep ?? 0m, futuresSymbol.MinTradeQuantity ?? 0m, futuresSymbol.MinNotionalValue ?? 0m, discipline);
        var riskError = TradingRiskPolicy.ValidateOpen(decision, config.RiskControl, context, out var plan);
        if (riskError is not null || plan is null)
        {
            Audit(db, trader.Id, symbol.ToString(), actionText, null, price, false, "策略风控拒绝开仓", riskError);
            return;
        }
        var quantity = plan.Quantity;
        var stopLossPrice = plan.StopLossPrice;
        var takeProfitPrice = plan.TakeProfitPrice;
        if (!audit.TakeProfitSupported)
        {
            Audit(db, trader.Id, symbol.ToString(), actionText, null, price, false, "交易所不支持附加止盈，无法执行当前退出方案", null);
            return;
        }

        var gateway = new ExchangeOpenOrderGateway(account, symbol, trader.IsCrossMargin, trading);
        var (ok, error) = await OpenOrderSubmission.SubmitAsync(gateway, plan, isLong, ct);

        Audit(
            db, trader.Id, symbol.ToString(), actionText, quantity, price, ok,
            ok ? (isLong ? "市价开多" : "市价开空") : null,
            error,
            stopLossPrice, takeProfitPrice);
        Console.WriteLine(ok
            ? $"[Trader] #{trader.Id} 下单成功：{actionText} {quantity} {symbol} @{price}（止损 {stopLossPrice:0.########}）"
            : $"[Trader] #{trader.Id} 下单失败：{error}");
    }

    private static async Task ExecuteCloseAsync(
        AppDbContext db,
        Trader trader,
        ExchangeAccount account,
        CryptoExchange.Net.SharedApis.SharedSymbol symbol,
        ExchangeTradingService trading,
        CancellationToken ct)
    {
        if (trader.ExecutionMode != "live")
        {
            Audit(db, trader.Id, symbol.ToString(), "close", null, null, true, "演练模式：未平仓", null);
            return;
        }

        var (ok, error, closeQuantity, closePrice) = await trading.ClosePositionAsync(account, symbol, trader.IsCrossMargin, ct);
        Audit(db, trader.Id, symbol.ToString(), "close", closeQuantity, closePrice, ok, ok ? "整仓平仓" : null, error);
        Console.WriteLine(ok
            ? $"[Trader] #{trader.Id} 平仓成功：{symbol}"
            : $"[Trader] #{trader.Id} 平仓失败：{error}");
    }

    // 可用资金可能包含未实现盈亏；同时保留持仓初始保证金下界，不能因浮盈把已占用误算为0。
    internal static decimal UsedMargin(AccountSnapshot account, IReadOnlyList<SymbolPosition> positions)
    {
        var held = positions.Sum(p => p.Leverage is > 0 ? Math.Abs(p.Quantity * p.AverageOpenPrice) / p.Leverage.Value : account.UsdtEquity);
        return Math.Max(held, Math.Max(0m, account.UsdtEquity + positions.Sum(p => p.UnrealizedPnl ?? 0m) - account.UsdtAvailable));
    }

    // 审计行标的为「BASE/QUOTE」、交易所持仓为原生名（如 BASEUSDT）：统一取基准资产做匹配键
    private static string BaseAssetOf(string symbol) =>
        symbol.Contains('/')
            ? symbol.Split('/')[0].Trim().ToUpperInvariant()
            : (symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? symbol[..^4].ToUpperInvariant() : symbol.ToUpperInvariant());

    private static void Audit(
        AppDbContext db,
        int traderId,
        string? symbol,
        string action,
        decimal? quantity,
        decimal? price,
        bool success,
        string? note,
        string? error,
        decimal? stopLossPrice = null,
        decimal? takeProfitPrice = null)
    {
        db.TraderOrders.Add(new TraderOrder
        {
            TraderId = traderId,
            At = DateTime.UtcNow,
            Symbol = symbol,
            Action = action,
            Quantity = quantity,
            Price = price,
            StopLossPrice = stopLossPrice,
            TakeProfitPrice = takeProfitPrice,
            Success = success,
            Note = note,
            Error = error,
        });
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }
}
