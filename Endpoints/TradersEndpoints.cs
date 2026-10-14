using CryptoExchange.Net.SharedApis;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;

namespace LuckyCat.Endpoints;

public static class TradersEndpoints
{
    public static RouteGroupBuilder MapTraders(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/traders");

        group.MapGet("/", async (AppDbContext db) =>
        {
            var items = await db.Traders.OrderByDescending(t => t.Id).ToListAsync();
            var providers = await db.Providers.ToDictionaryAsync(p => p.Id);
            var accounts = await db.ExchangeAccounts.ToDictionaryAsync(a => a.Id);
            var strategies = await db.Strategies.ToDictionaryAsync(s => s.Id);
            return Results.Ok(items.Select(t => ToDto(t, providers, accounts, strategies)));
        });

        group.MapPost("/", async (TraderUpsertRequest request, AppDbContext db) =>
        {
            var error = await ValidateAsync(request, db);
            if (error is not null) return Results.BadRequest(new { message = error });

            var now = DateTime.UtcNow;
            var trader = new Trader
            {
                Name = request.Name.Trim(),
                ProviderId = request.ProviderId,
                ExchangeAccountId = request.ExchangeAccountId,
                StrategyId = request.StrategyId,
                IsRunning = false,
                ExecutionMode = NormalizeMode(request.ExecutionMode),
                IsCrossMargin = request.IsCrossMargin,
                ScanIntervalMinutes = Math.Clamp(request.ScanIntervalMinutes, 3, 60),
                InitialBalance = request.InitialBalance,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Traders.Add(trader);
            await db.SaveChangesAsync();

            var providers = await db.Providers.ToDictionaryAsync(p => p.Id);
            var accounts = await db.ExchangeAccounts.ToDictionaryAsync(a => a.Id);
            var strategies = await db.Strategies.ToDictionaryAsync(s => s.Id);
            return Results.Created($"/api/traders/{trader.Id}", ToDto(trader, providers, accounts, strategies));
        });

        group.MapPut("/{id:int}", async (int id, TraderUpsertRequest request, AppDbContext db, TraderRunnerService runner) =>
        {
            var trader = await db.Traders.FindAsync(new object[] { id });
            if (trader is null) return Results.NotFound();

            var error = await ValidateAsync(request, db);
            if (error is not null) return Results.BadRequest(new { message = error });

            trader.Name = request.Name.Trim();
            trader.ProviderId = request.ProviderId;
            trader.ExchangeAccountId = request.ExchangeAccountId;
            trader.StrategyId = request.StrategyId;
            trader.ExecutionMode = NormalizeMode(request.ExecutionMode);
            trader.IsCrossMargin = request.IsCrossMargin;
            trader.ScanIntervalMinutes = Math.Clamp(request.ScanIntervalMinutes, 3, 60);
            trader.InitialBalance = request.InitialBalance;
            trader.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            // 运行中：以新配置重启循环（新间隔/新绑定立即生效）
            if (trader.IsRunning)
            {
                runner.Restart(trader.Id);
            }

            var providers = await db.Providers.ToDictionaryAsync(p => p.Id);
            var accounts = await db.ExchangeAccounts.ToDictionaryAsync(a => a.Id);
            var strategies = await db.Strategies.ToDictionaryAsync(s => s.Id);
            return Results.Ok(ToDto(trader, providers, accounts, strategies));
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, TraderRunnerService runner) =>
        {
            var trader = await db.Traders.FindAsync(new object[] { id });
            if (trader is null) return Results.NotFound();

            runner.Stop(id);
            db.Traders.Remove(trader);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/start", async (int id, AppDbContext db, TraderRunnerService runner, SseEventHub hub) =>
        {
            var trader = await db.Traders.FindAsync(new object[] { id });
            if (trader is null) return Results.NotFound();

            // 实盘模式：启动前校验交易所能力（下标单/持仓/余额/止损支持，缺一拒绝）
            if (trader.ExecutionMode == "live")
            {
                var account = await db.ExchangeAccounts.FindAsync(new object[] { trader.ExchangeAccountId });
                if (account is null) return Results.BadRequest(new { message = "绑定的交易所账户不存在" });

                var audit = ExchangeTradingService.Audit(account);
                if (!audit.CanTrade)
                {
                    return Results.BadRequest(new { message = $"实盘启动被拒：{string.Join("；", audit.Notes)}" });
                }
            }

            trader.IsRunning = true;
            trader.StartedAt = DateTime.UtcNow;
            trader.LastCycleStatus = null;
            trader.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            runner.Start(trader.Id);
            hub.Publish(SseChannels.Trader(trader.Id), new SseEvent("cycle", trader));
            return Results.Ok(new { running = true });
        });

        group.MapPost("/{id:int}/stop", async (int id, AppDbContext db, TraderRunnerService runner, SseEventHub hub) =>
        {
            var trader = await db.Traders.FindAsync(new object[] { id });
            if (trader is null) return Results.NotFound();

            runner.Stop(id);
            trader.IsRunning = false;
            trader.StartedAt = null;
            trader.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            hub.Publish(SseChannels.Trader(trader.Id), new SseEvent("cycle", trader));
            return Results.Ok(new { running = false });
        });

        // 手动平仓（看板持仓行）：仅实盘模式；标的名支持原生名或 BASE/QUOTE
        group.MapPost("/{id:int}/close-position", async (int id, TraderClosePositionRequest request, AppDbContext db, ExchangeTradingService trading, SseEventHub hub, CancellationToken ct) =>
        {
            var trader = await db.Traders.FindAsync(new object[] { id }, ct);
            if (trader is null) return Results.NotFound();

            var account = await db.ExchangeAccounts.FindAsync(new object[] { trader.ExchangeAccountId }, ct);
            if (account is null) return Results.BadRequest(new { message = "绑定的交易所账户不存在" });
            if (trader.ExecutionMode != "live") return Results.BadRequest(new { message = "演练模式不执行平仓" });

            var symbol = await ExchangeTradingService.ResolveSymbolByNameAsync(account, request.Symbol, ct)
                ?? CoinRankingService.ParseConfiguredSymbol(request.Symbol);
            // 平仓前先采样当前持仓（被平仓位的历史杠杆回填用）
            await trading.CaptureSnapshotsAsync(db, account, ct);
            var (ok, closeError, closeQuantity, closePrice) = await trading.ClosePositionAsync(account, symbol, trader.IsCrossMargin, ct);

            // 手动平仓同样留痕（与后台执行共用审计表；数量/价格为被平仓位与平仓时点值）
            try
            {
                var auditRow = new TraderOrder
                {
                    TraderId = trader.Id,
                    At = DateTime.UtcNow,
                    Symbol = request.Symbol,
                    Action = "close",
                    Quantity = closeQuantity,
                    Price = closePrice,
                    Success = ok,
                    Note = ok ? "手动平仓（看板）" : null,
                    Error = closeError,
                };
                db.TraderOrders.Add(auditRow);
                await db.SaveChangesAsync(ct);
                hub.Publish(SseChannels.Trader(trader.Id), new SseEvent("order", auditRow));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Trading] 手动平仓审计落库失败：{ex.Message}");
            }

            return ok
                ? Results.Ok(new { closed = true })
                : Results.BadRequest(new { message = closeError ?? "平仓失败" });
        });

        // 看板聚合：交易员信息 + 能力审计 + 实时账户/持仓（10s 缓存）+ 净值样本 + 决策 + 自维护仓位历史 + 订单审计
        group.MapGet("/{id:int}/dashboard", async (int id, AppDbContext db, ExchangeTradingService trading, PositionHistorySyncService historySync, CancellationToken ct) =>
        {
            var trader = await db.Traders.FindAsync(new object[] { id }, ct);
            if (trader is null) return Results.NotFound();

            var provider = await db.Providers.FindAsync(new object[] { trader.ProviderId }, ct);
            var account = await db.ExchangeAccounts.FindAsync(new object[] { trader.ExchangeAccountId }, ct);
            var strategy = await db.Strategies.FindAsync(new object[] { trader.StrategyId }, ct);

            var strategyType = "ai_trading";
            if (strategy is not null)
            {
                try
                {
                    var config = JsonSerializer.Deserialize<StrategyConfig>(strategy.ConfigJson, VerificationJson.Options);
                    strategyType = config?.StrategyType ?? "ai_trading";
                }
                catch (JsonException)
                {
                    // 配置解析失败时按默认类型展示
                }
            }

            var audit = account is null
                ? new TradingAudit(false, false, false, false, false, false, ["绑定账户缺失"])
                : ExchangeTradingService.Audit(account);

            var positionMode = account is null
                ? null
                : (await trading.GetPositionModeAsync(account, ct)) switch
                {
                    SharedPositionMode.HedgeMode => "双向",
                    SharedPositionMode.OneWay => "单向",
                    _ => null,
                };

            var info = new DashboardTraderInfo(
                trader.Id,
                trader.Name,
                provider?.Name ?? "（已删除）",
                provider?.Model ?? "",
                account?.ExchangeType.ToString() ?? "",
                account?.AccountName ?? "",
                strategy?.Name ?? "（已删除）",
                strategyType,
                trader.IsRunning,
                trader.ExecutionMode,
                trader.IsCrossMargin,
                trader.ScanIntervalMinutes,
                trader.InitialBalance,
                trader.StartedAt,
                trader.LastCycleAt,
                trader.LastCycleStatus,
                audit.CanTrade,
                audit.Notes,
                positionMode);

            // 账户与持仓（真实；读取失败如实返回错误）
            DashboardAccount? accountDto = null;
            IReadOnlyList<DashboardPosition> positions = [];
            if (account is not null)
            {
                var snapshot = await trading.GetAccountAsync(account, ct);
                var livePositions = await trading.GetPositionsAsync(account, ct);
                positions = livePositions
                    .Select(p => new DashboardPosition(p.Symbol, p.Side.ToString(), p.Quantity, p.AverageOpenPrice, p.UnrealizedPnl, p.LiquidationPrice, p.Leverage))
                    .ToList();

                decimal? pnl = null;
                double? pnlPct = null;
                if (snapshot.Success && trader.InitialBalance is { } initial && initial > 0)
                {
                    pnl = Math.Round(snapshot.UsdtEquity - initial, 2);
                    pnlPct = Math.Round((double)((snapshot.UsdtEquity - initial) / initial * 100m), 2);
                }

                // 保证金占用 = Σ(数量 × 均价 / 杠杆) / 净值
                decimal? marginPct = null;
                if (snapshot.Success && snapshot.UsdtEquity > 0)
                {
                    var used = positions
                        .Where(p => p.Leverage is > 0)
                        .Sum(p => p.Quantity * p.AverageOpenPrice / p.Leverage!.Value);
                    marginPct = Math.Round(used / snapshot.UsdtEquity * 100m, 2);
                }

                accountDto = new DashboardAccount(
                    snapshot.Success, snapshot.Error,
                    snapshot.Success ? snapshot.UsdtEquity : null,
                    snapshot.Success ? snapshot.UsdtAvailable : null,
                    pnl, pnlPct,
                    positions.Count,
                    marginPct,
                    DateTime.UtcNow);
            }

            // 净值样本（账户级，最近 200 个，时间升序供绘图；由同步器每约 60 秒采样）
            IReadOnlyList<DashboardSample> samples = account is null
                ? []
                : (await db.AccountEquitySamples
                        .Where(s => s.ExchangeAccountId == account.Id)
                        .OrderByDescending(s => s.Id)
                        .Take(200)
                        .ToListAsync(ct))
                    .OrderBy(s => s.At)
                    .Select(s => new DashboardSample(s.At, s.Equity))
                    .ToList();

            // 最近决策（含思维链/用量；与 SSE 推送共用同一 DTO 构造，保证两路数据同构）
            var decisions = await LoadDecisionsAsync(db, id, 50, ct);

            // 持仓采样（供历史杠杆回填）→ 触发一次自维护同步 → 历史仓位读本系统的库（不依赖交易所保留窗口）
            string? syncError = null;
            if (account is not null)
            {
                await trading.CaptureSnapshotsAsync(db, account, ct);
                syncError = await historySync.SyncAccountAsync(db, account, ct);
            }
            var history = account is null
                ? EmptyHistory("绑定账户缺失")
                : await BuildHistoryAsync(db, account, trader.InitialBalance, syncError, ct);

            // 历史仓位「杠杆」回填：Binance 等对已平仓位不提供杠杆，用持仓期间采样值补（仅补空值）
            if (account is not null && history.Rows.Any(r => r.Leverage is null))
            {
                var snapshots = await db.AccountPositionSnapshots
                    .Where(s => s.ExchangeAccountId == account.Id && s.Leverage != null)
                    .OrderByDescending(s => s.Id)
                    .Take(500)
                    .ToListAsync(ct);
                if (snapshots.Count > 0)
                {
                    var bySymbol = snapshots
                        .GroupBy(s => s.Symbol, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.At).ToList(), StringComparer.Ordinal);
                    var updatedRows = history.Rows
                        .Select(r =>
                        {
                            if (r.Leverage is not null || !bySymbol.TryGetValue(r.Symbol, out var list)) return r;
                            var hit = list.FirstOrDefault(s => s.At <= r.CloseTime && (r.Side is null || s.Side is null || s.Side == r.Side));
                            return hit is null ? r : r with { Leverage = hit.Leverage };
                        })
                        .ToList();
                    var leverageNote = "杠杆取自持仓期间采样（每轮运行/查看看板时采样）";
                    history = history with
                    {
                        Rows = updatedRows,
                        Note = history.Note is null ? leverageNote : $"{history.Note}；{leverageNote}",
                    };
                }
            }

            // 仍缺杠杆的行：回填该标的「当前杠杆设置」（Binance 官网仓位历史同口径；开仓后改过杠杆则以当前为准）
            if (account is not null && account.ExchangeType == ExchangeType.Binance && history.Rows.Any(r => r.Leverage is null))
            {
                var leverageMap = await trading.GetBinanceLeverageMapAsync(account, history.Rows.Select(r => r.Symbol).Distinct().ToList(), ct);
                if (leverageMap.Count > 0)
                {
                    var nullsBefore = history.Rows.Count(r => r.Leverage is null);
                    var filledRows = history.Rows
                        .Select(r => r.Leverage is null && leverageMap.TryGetValue(r.Symbol, out var level) ? r with { Leverage = level } : r)
                        .ToList();
                    if (filledRows.Count(r => r.Leverage is null) < nullsBefore)
                    {
                        var levelNote = "无采样记录的杠杆取该标的当前杠杆设置（与 Binance 官网仓位历史同口径；开仓后改过杠杆则以当前为准）";
                        history = history with
                        {
                            Rows = filledRows,
                            Note = history.Note is null ? levelNote : $"{history.Note}；{levelNote}",
                        };
                    }
                }
            }

            var orders = (await db.TraderOrders
                    .Where(o => o.TraderId == id)
                    .OrderByDescending(o => o.Id)
                    .Take(20)
                    .ToListAsync(ct))
                .Select(ToOrderDto)
                .ToList();

            return Results.Ok(new TraderDashboardDto(info, accountDto, samples, positions, decisions, history, orders));
        });

        // 实时推送（SSE）：持仓/余额由交易所 WS 订阅驱动、行情 ticker 驱动未实现盈亏；按状态版本节流（1 秒巡检）；
        // 决策/审计/周期状态/净值样本/历史信号来自 SseEventHub（模块落库后发布，连接只订阅转发，不做轮询）
        group.MapGet("/{id:int}/stream", async (int id, AppDbContext db, AccountStreamService stream, SseEventHub hub, HttpContext http, CancellationToken ct) =>
        {
            var trader = await db.Traders.FindAsync(new object[] { id }, ct);
            if (trader is null) return Results.NotFound();
            var account = await db.ExchangeAccounts.FindAsync(new object[] { trader.ExchangeAccountId }, ct);
            if (account is null) return Results.NotFound();

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // 先订阅再播种：订阅之后产生的事件进入通道不丢失（与种子帧的重叠记录由前端按 Id 合并）
            var (traderEvents, traderSubscription) = hub.Subscribe(SseChannels.Trader(id));
            var (accountEvents, accountSubscription) = hub.Subscribe(SseChannels.Account(account.Id));
            await stream.AcquireAsync(account, ct);
            try
            {
                // 种子帧：连接建立时补发最近决策；此后仅随事件推送
                var seedDecisions = await LoadDecisionsAsync(db, id, 10, ct);
                if (seedDecisions.Count > 0)
                {
                    var seedJson = JsonSerializer.Serialize(new { decisions = seedDecisions }, FrameJson);
                    await http.Response.WriteAsync($"event: decisions\ndata: {seedJson}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }

                var lastVersion = -1;
                var lastWriteAt = Environment.TickCount64;
                while (!ct.IsCancellationRequested)
                {
                    // 事件帧：即时转发（巡检节拍 1 秒，事件送达延迟上限 1 秒）
                    while (traderEvents.TryRead(out var evt) || accountEvents.TryRead(out evt))
                    {
                        if (ToEventFrame(evt) is not { } frame) continue;
                        await http.Response.WriteAsync($"event: {frame.Name}\ndata: {frame.Json}\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        lastWriteAt = Environment.TickCount64;
                    }

                    var state = stream.GetState(account.Id);
                    if (state is not null && state.Version != lastVersion)
                    {
                        lastVersion = state.Version;
                        var payload = new
                        {
                            version = state.Version,
                            at = state.At,
                            equity = state.Equity,
                            available = state.Available,
                            priceType = state.PriceType,
                            marks = state.Marks,
                            positions = state.Positions.Select(p => new
                            {
                                symbol = p.Symbol,
                                side = p.Side.ToString(),
                                quantity = p.Quantity,
                                averageOpenPrice = p.AverageOpenPrice,
                                unrealizedPnl = p.UnrealizedPnl,
                                liquidationPrice = p.LiquidationPrice,
                                leverage = p.Leverage,
                            }),
                        };
                        var json = JsonSerializer.Serialize(payload, FrameJson);
                        await http.Response.WriteAsync($"event: state\ndata: {json}\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        lastWriteAt = Environment.TickCount64;
                    }

                    // 心跳帧：每 15 秒（静默期）写一帧轻量事件——防代理空闲超时掐断长连接，客户端据此判活
                    if (Environment.TickCount64 - lastWriteAt >= 15000)
                    {
                        var pingJson = JsonSerializer.Serialize(new { at = DateTime.UtcNow }, FrameJson);
                        await http.Response.WriteAsync($"event: ping\ndata: {pingJson}\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        lastWriteAt = Environment.TickCount64;
                    }

                    await Task.Delay(1000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端断开
            }
            finally
            {
                traderSubscription.Dispose();
                accountSubscription.Dispose();
                stream.Release(account.Id);
            }

            return Results.Empty;
        });

        return group;
    }

    // SSE 帧序列化与看板 HTTP 载荷**同构**：不省略 null 字段——前端按 `=== null` 判空（盈亏比/信心度等），
    // 字段缺失会让判空失效（undefined 绕过判空，曾致决策卡显示「1 : NaN」）
    private static readonly JsonSerializerOptions FrameJson = new(JsonSerializerDefaults.Web);

    // 最近决策 DTO：价格与风控字段从落库 JSON 提取；看板聚合、SSE 种子帧与事件帧共用（三路数据同构，前端可按 Id 合并）
    private static async Task<List<DashboardDecision>> LoadDecisionsAsync(AppDbContext db, int traderId, int take, CancellationToken ct)
    {
        var records = await db.DecisionRecords
            .Where(r => r.TraderId == traderId)
            .OrderByDescending(r => r.Id)
            .Take(take)
            .ToListAsync(ct);
        return records.Select(ToDecisionDto).ToList();
    }

    internal static DashboardDecision ToDecisionDto(DecisionRecord r)
    {
        decimal? entry = null, sl = null, tp = null, posPct = null, lev = null;
        string? reason = null;
        try
        {
            using var doc = JsonDocument.Parse(r.DecisionJson);
            var root = doc.RootElement;
            entry = ReadDecimal(root, "entry_price");
            sl = ReadDecimal(root, "stop_loss_pct");
            tp = ReadDecimal(root, "take_profit_pct");
            posPct = ReadDecimal(root, "position_pct");
            lev = ReadDecimal(root, "leverage");
            reason = ReadString(root, "reason");
        }
        catch (JsonException)
        {
            // 落库 JSON 异常时仅展示基础字段
        }

        var round = DecisionRoundMetadata.Read(r);
        return new DashboardDecision(r.Id, r.CreatedAt, r.Action, r.Symbol, r.Confidence,
            entry, sl, tp, posPct, lev, reason, r.Reasoning, r.InputTokens, r.OutputTokens, r.ElapsedMs, r.TraderId,
            r.SystemPrompt, r.UserPrompt, round?.Status ?? (r.Action == "error" ? "error" : "ok"),
            round?.FailureStage, round?.Error, round?.RawOutput, round?.PromptRevision, round?.RequestSent);
    }

    private static TraderOrderDto ToOrderDto(TraderOrder o) =>
        new(o.Id, o.At, o.Symbol, o.Action, o.Quantity, o.Price, o.Success, o.Note, o.Error);

    // 总线事件 → SSE 帧（域对象在此格式化；未知事件忽略，便于后续扩展新事件类型）
    private static (string Name, string Json)? ToEventFrame(SseEvent evt) => evt switch
    {
        { Name: "decision", Payload: DecisionRecord r } =>
            ("decisions", JsonSerializer.Serialize(new { decisions = new[] { ToDecisionDto(r) } }, FrameJson)),
        { Name: "order", Payload: TraderOrder o } =>
            ("order", JsonSerializer.Serialize(ToOrderDto(o), FrameJson)),
        { Name: "cycle", Payload: Trader t } =>
            ("cycle", JsonSerializer.Serialize(new { at = t.LastCycleAt, status = t.LastCycleStatus, isRunning = t.IsRunning }, FrameJson)),
        { Name: "equity", Payload: AccountEquitySample s } =>
            ("equity", JsonSerializer.Serialize(new { at = s.At, equity = s.Equity }, FrameJson)),
        { Name: "history" } => ("history", "{}"),
        _ => null,
    };

    // 历史仓位（自维护库读取：PositionHistories 由同步器从交易所成交重建/共享接口增量落库）
    private static async Task<DashboardHistory> BuildHistoryAsync(AppDbContext db, ExchangeAccount account, decimal? initialBalance, string? syncError, CancellationToken ct)
    {
        var records = await db.PositionHistories
            .Where(p => p.ExchangeAccountId == account.Id)
            .OrderByDescending(p => p.CloseTime)
            .Take(100)
            .ToListAsync(ct);
        if (records.Count == 0)
        {
            var emptyNote = syncError is null
                ? "该账户尚无历史仓位记录（同步器将从交易所成交明细累积）"
                : $"本次同步失败：{syncError}";
            return EmptyHistory(null, emptyNote);
        }

        var mapped = records
            .Select(p => new DashboardHistoryRow(
                p.Symbol,
                p.Side,
                p.EntryPrice,
                p.ExitPrice,
                p.Quantity,
                p.Leverage,
                p.RealizedPnl,
                p.Fee,
                p.CloseTime))
            .ToList();

        // 时间升序供回撤/夏普计算
        var asc = mapped.OrderBy(r => r.CloseTime).ToList();
        var wins = asc.Where(r => r.RealizedPnl > 0).ToList();
        var losses = asc.Where(r => r.RealizedPnl < 0).ToList();
        var totalPnl = asc.Sum(r => r.RealizedPnl);
        var totalFee = asc.Sum(r => r.Fee ?? 0m);
        var winSum = wins.Sum(r => r.RealizedPnl);
        var lossSum = Math.Abs(losses.Sum(r => r.RealizedPnl));

        var metrics = new DashboardMetrics(
            asc.Count,
            asc.Count > 0 ? Math.Round((double)wins.Count / asc.Count * 100, 2) : 0,
            Math.Round(totalPnl, 4),
            Math.Round(totalFee, 4),
            Math.Round(totalPnl - totalFee, 4),
            lossSum > 0 ? Math.Round(winSum / lossSum, 2) : (winSum > 0 ? 999m : 0m),
            wins.Count > 0 ? Math.Round(winSum / wins.Count, 4) : 0m,
            losses.Count > 0 ? Math.Round(losses.Sum(r => r.RealizedPnl) / losses.Count, 4) : 0m,
            ComputeMaxDrawdownPct(asc, initialBalance),
            ComputeSharpe(asc));

        var directions = asc
            .GroupBy(r => r.Side ?? "", StringComparer.Ordinal)
            .Select(g => new DashboardDirectionStat(
                g.Key,
                g.Count(),
                Math.Round((double)g.Count(r => r.RealizedPnl > 0) / g.Count() * 100, 2),
                Math.Round(g.Sum(r => r.RealizedPnl), 4),
                Math.Round(g.Average(r => r.RealizedPnl), 4)))
            .ToList();

        var symbols = asc
            .GroupBy(r => r.Symbol, StringComparer.Ordinal)
            .Select(g => new DashboardSymbolStat(
                g.Key,
                g.Count(),
                Math.Round((double)g.Count(r => r.RealizedPnl > 0) / g.Count() * 100, 2),
                Math.Round(g.Sum(r => r.RealizedPnl), 4)))
            .OrderByDescending(s => s.Count)
            .ToList();

        // 来源口径说明（按落库行来源合成）
        var sources = records.Select(p => p.Source).Distinct(StringComparer.Ordinal).ToList();
        var note = sources.Contains("binance-trades", StringComparer.Ordinal)
            ? "数据来源：本系统维护的仓位历史（同步自交易所成交明细 FIFO 重建；未平仓位不计入；非 USDT 计价手续费未计入）"
            : "数据来源：本系统维护的仓位历史（同步自交易所共享接口持仓历史）";
        if (syncError is not null)
        {
            note += $"；本次同步失败：{syncError}（展示最近一次已同步数据）";
        }

        // 展示按时间倒序
        mapped = asc.OrderByDescending(r => r.CloseTime).ToList();
        return new DashboardHistory(true, null, metrics, directions, symbols, mapped, note);
    }

    private static DashboardHistory EmptyHistory(string? error, string? note = null) =>
        new(false, error,
            new DashboardMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            [], [], [], note);

    // 回撤：已实现盈亏累计曲线的峰值回撤；百分比相对初始余额（缺失时回退为峰值绝对值）——
    // 直接以峰值为分母在峰值很小时会得出无意义的百分比
    private static double ComputeMaxDrawdownPct(IReadOnlyList<DashboardHistoryRow> asc, decimal? initialBalance)
    {
        var cum = 0m;
        var peak = 0m;
        var maxDd = 0m;
        foreach (var row in asc)
        {
            cum += row.RealizedPnl;
            peak = Math.Max(peak, cum);
            if (peak > 0)
            {
                maxDd = Math.Max(maxDd, peak - cum);
            }
        }

        var baseValue = initialBalance is > 0 ? initialBalance.Value : peak;
        return baseValue > 0 ? Math.Round((double)(maxDd / baseValue * 100m), 2) : 0;
    }

    // 夏普（近似口径：按笔已实现盈亏的均值/标准差 × √笔数；样本不足或零方差时为 0）
    private static double ComputeSharpe(IReadOnlyList<DashboardHistoryRow> asc)
    {
        if (asc.Count < 2) return 0;
        var values = asc.Select(r => (double)r.RealizedPnl).ToList();
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        var std = Math.Sqrt(variance);
        if (std <= 0) return 0;
        return Math.Round(mean / std * Math.Sqrt(values.Count), 2);
    }

    private static string NormalizeMode(string? mode) => mode == "dry" ? "dry" : "live";

    private static async Task<string?> ValidateAsync(TraderUpsertRequest request, AppDbContext db)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "名称不能为空";
        if (!await db.Providers.AnyAsync(p => p.Id == request.ProviderId)) return "选择的模型提供商不存在";
        if (!await db.ExchangeAccounts.AnyAsync(a => a.Id == request.ExchangeAccountId)) return "选择的交易所账户不存在";
        if (!await db.Strategies.AnyAsync(s => s.Id == request.StrategyId)) return "选择的策略不存在";
        if (request.ScanIntervalMinutes is < 3 or > 60) return "扫描间隔需在 3–60 分钟之间";
        if (request.InitialBalance is < 0) return "初始余额不能为负";
        return null;
    }

    private static TraderDto ToDto(
        Trader t,
        Dictionary<int, Provider> providers,
        Dictionary<int, ExchangeAccount> accounts,
        Dictionary<int, Strategy> strategies)
    {
        providers.TryGetValue(t.ProviderId, out var provider);
        accounts.TryGetValue(t.ExchangeAccountId, out var account);
        strategies.TryGetValue(t.StrategyId, out var strategy);
        return new TraderDto(
            t.Id, t.Name,
            t.ProviderId, provider?.Name ?? "（已删除）", provider?.Model ?? "",
            t.ExchangeAccountId, account?.ExchangeType ?? ExchangeType.Binance, account?.AccountName ?? "（已删除）",
            t.StrategyId, strategy?.Name ?? "（已删除）",
            t.IsRunning, t.ExecutionMode, t.IsCrossMargin, t.ScanIntervalMinutes, t.InitialBalance,
            t.StartedAt, t.LastCycleAt, t.LastCycleStatus, t.CreatedAt, t.UpdatedAt);
    }

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

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
