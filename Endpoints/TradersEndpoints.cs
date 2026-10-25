using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CryptoExchange.Net.SharedApis;
using Microsoft.EntityFrameworkCore;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Tools;
using LuckyCat.Workflows;

namespace LuckyCat.Endpoints;

/// <summary>
/// 交易员端点（LC-STD-03 §3.4；Task 14 B 组接新链路）：
/// 启停/手动平仓/复盘触发的调度一律走 <see cref="TradingCycleRunner"/>/<see cref="ReviewRunner"/>——
/// 旧 TraderRunnerService 的 hosted 注册已随切流摘除（R-14-5，本仓库恰好一条调度链）。
/// 启停按状态幂等（200）；start 前置 provider 工具能力闸（422 provider_capability_missing，API-09）；
/// stop 取消该交易员当前周期 token，周期按约定记 aborted + cancelled_by_user（LC-STD-01 §8.6）；
/// GET 端点无副作用：dashboard/stream 不触发任何周期（stream 只订阅转发）。
/// </summary>
public static class TradersEndpoints
{
    /// <summary>复盘重复触发窗口（秒，LC-STD-03 §1.4/§6：60 秒内重复触发 → 409 review_in_progress）。</summary>
    private const int ReviewTriggerCooldownSeconds = 60;

    extension(IEndpointRouteBuilder app)
    {
        public RouteGroupBuilder MapTraders()
        {
            var group = app.MapGroup("/api/traders");

            group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            {
                var items = await db.Traders.OrderByDescending(t => t.Id).ToListAsync(ct);
                var providers = await db.Providers.ToDictionaryAsync(p => p.Id, ct);
                var accounts = await db.ExchangeAccounts.ToDictionaryAsync(a => a.Id, ct);
                var strategies = await db.Strategies.ToDictionaryAsync(s => s.Id, ct);
                return ApiResults.Ok(items.Select(t => ToDto(t, providers, accounts, strategies)).ToList());
            });

            group.MapPost("/", async (TraderUpsertRequest request, AppDbContext db, CancellationToken ct) =>
            {
                var error = await ValidateAsync(request, db, ct);
                if (error is not null) return ApiResults.Fail("validation_failed", error, 400);

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
                await db.SaveChangesAsync(ct);

                var providers = await db.Providers.ToDictionaryAsync(p => p.Id, ct);
                var accounts = await db.ExchangeAccounts.ToDictionaryAsync(a => a.Id, ct);
                var strategies = await db.Strategies.ToDictionaryAsync(s => s.Id, ct);
                return ApiResults.Created(ToDto(trader, providers, accounts, strategies));
            });

            group.MapPut("/{id:int}", async (int id, TraderUpsertRequest request, AppDbContext db,
                TradingCycleRunner runner, CancellationToken ct) =>
            {
                var trader = await db.Traders.FindAsync(new object[] { id }, ct);
                if (trader is null) return ApiResults.Fail("not_found", "交易员不存在", 404);

                var error = await ValidateAsync(request, db, ct);
                if (error is not null) return ApiResults.Fail("validation_failed", error, 400);

                trader.Name = request.Name.Trim();
                trader.ProviderId = request.ProviderId;
                trader.ExchangeAccountId = request.ExchangeAccountId;
                trader.StrategyId = request.StrategyId;
                trader.ExecutionMode = NormalizeMode(request.ExecutionMode);
                trader.IsCrossMargin = request.IsCrossMargin;
                trader.ScanIntervalMinutes = Math.Clamp(request.ScanIntervalMinutes, 3, 60);
                trader.InitialBalance = request.InitialBalance;
                trader.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                // 运行中：以新配置重启循环（新间隔/新绑定立即生效）——仍是唯一调度链（R-14-5）。
                if (trader.IsRunning) runner.Restart(trader.Id);

                var providers = await db.Providers.ToDictionaryAsync(p => p.Id, ct);
                var accounts = await db.ExchangeAccounts.ToDictionaryAsync(a => a.Id, ct);
                var strategies = await db.Strategies.ToDictionaryAsync(s => s.Id, ct);
                return ApiResults.Ok(ToDto(trader, providers, accounts, strategies));
            });

            group.MapDelete("/{id:int}", async (int id, AppDbContext db, CancellationToken ct) =>
            {
                var trader = await db.Traders.FindAsync(new object[] { id }, ct);
                if (trader is null) return ApiResults.Fail("not_found", "交易员不存在", 404);

                if (trader.IsRunning)
                {
                    return ApiResults.Fail("conflict", "交易员正在运行，请先停止再删除", 409);
                }

                // 已有周期或订单审计历史的交易员不得硬删除（LC-STD-03 §1.4：审计链完整，用停止代替）。
                if (await db.TradingCycles.AnyAsync(c => c.TraderId == id, ct)
                    || await db.TraderOrders.AnyAsync(o => o.TraderId == id, ct))
                {
                    return ApiResults.Fail("resource_in_use",
                        "该交易员已有周期或订单审计历史，不得硬删除，请以停止代替", 409);
                }

                db.Traders.Remove(trader);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            });

            group.MapPost("/{id:int}/start", async (int id, AppDbContext db, TradingCycleRunner runner,
                SseEventHub hub, CancellationToken ct) =>
            {
                var trader = await db.Traders.FindAsync(new object[] { id }, ct);
                if (trader is null) return ApiResults.Fail("not_found", "交易员不存在", 404);

                var provider = await db.Providers.FindAsync(new object[] { trader.ProviderId }, ct);
                if (provider is null)
                {
                    return ApiResults.Fail("validation_failed", "绑定的模型提供商不存在，请先完成绑定", 400);
                }

                // API-09：未通过工具调用验证（或编辑连接信息后被重置）的提供商不得启动。
                if (provider.ToolCallingSupported != true || provider.ToolCallingVerifiedAt is null)
                {
                    return ApiResults.Fail("provider_capability_missing",
                        "该提供商未通过工具调用验证，请先完成验证再启动交易员", 422);
                }

                // 实盘模式：启动前校验交易所能力（下标单/持仓/余额/止损支持，缺一拒绝）。
                if (trader.ExecutionMode == "live")
                {
                    var account = await db.ExchangeAccounts.FindAsync(new object[] { trader.ExchangeAccountId }, ct);
                    if (account is null) return ApiResults.Fail("validation_failed", "绑定的交易所账户不存在", 400);

                    var audit = ExchangeTradingService.Audit(account);
                    if (!audit.CanTrade)
                    {
                        return ApiResults.Fail("validation_failed",
                            $"实盘启动被拒：{string.Join("；", audit.Notes)}", 400);
                    }
                }

                if (!trader.IsRunning)
                {
                    trader.IsRunning = true;
                    trader.StartedAt = DateTime.UtcNow;
                    trader.LastCycleStatus = null;
                    trader.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }

                // 已在运行则幂等（Start 内部对同交易员去重，绝不起第二条循环）。
                runner.Start(trader.Id);
                hub.Publish(SseChannels.Trader(trader.Id), StateEvent(trader));

                var providers = await db.Providers.ToDictionaryAsync(p => p.Id, ct);
                var accounts = await db.ExchangeAccounts.ToDictionaryAsync(a => a.Id, ct);
                var strategies = await db.Strategies.ToDictionaryAsync(s => s.Id, ct);
                return ApiResults.Ok(ToDto(trader, providers, accounts, strategies));
            });

            group.MapPost("/{id:int}/stop", async (int id, AppDbContext db, TradingCycleRunner runner,
                SseEventHub hub, CancellationToken ct) =>
            {
                var trader = await db.Traders.FindAsync(new object[] { id }, ct);
                if (trader is null) return ApiResults.Fail("not_found", "交易员不存在", 404);

                // 停止语义（LC-STD-01 §8.6）：取消该交易员周期的 CancellationToken，终态由宿主写
                // aborted + cancelled_by_user；已停止时幂等（Stop 内部对缺失条目为空操作）。
                runner.Stop(trader.Id);
                if (trader.IsRunning)
                {
                    trader.IsRunning = false;
                    trader.StartedAt = null;
                    trader.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }

                hub.Publish(SseChannels.Trader(trader.Id), StateEvent(trader));

                var providers = await db.Providers.ToDictionaryAsync(p => p.Id, ct);
                var accounts = await db.ExchangeAccounts.ToDictionaryAsync(a => a.Id, ct);
                var strategies = await db.Strategies.ToDictionaryAsync(s => s.Id, ct);
                return ApiResults.Ok(ToDto(trader, providers, accounts, strategies));
            });

            // 手动平仓（§3.4）：创建 trigger=manual 周期，复用 ExecutionTools 写工具硬闸与审计。
            // R-14-1：字符串金额在创建任何行之前先解析；R-14-7：周期闸冲突 → 409 cycle_in_progress。
            group.MapPost("/{id:int}/close-position", async (int id, ManualCloseRequest request,
                AppDbContext db, TradingCycleRunner runner, CancellationToken ct) =>
            {
                var trader = await db.Traders.FindAsync(new object[] { id }, ct);
                if (trader is null) return ApiResults.Fail("not_found", "交易员不存在", 404);

                var validationError = ValidateManualClose(request);
                if (validationError is not null) return ApiResults.Fail("validation_failed", validationError, 400);

                string cycleId;
                try
                {
                    cycleId = await runner.CloseManuallyAsync(trader, request, ct);
                }
                catch (CycleInProgressException ex)
                {
                    // 已有周期在跑：不排队、不落行（Task 11 结转，§5.2 示例逐字文案）。
                    return ApiResults.Fail(ex.Code, "该交易员已有运行中的周期，请等待当前周期结束后再试", 409);
                }
                catch (FormatException ex)
                {
                    // 兜底：runner 内解析拒绝同样先于建行；原始文本只进日志（LC-STD-02）。
                    Console.WriteLine($"[Api] 手动平仓参数解析失败：{LogRedaction.Scrub(ex.Message)}");
                    return ApiResults.Fail("validation_failed", "quantity/price 必须是十进制数字字符串", 400);
                }

                var order = await db.TraderOrders.AsNoTracking()
                    .Where(o => o.CycleId == cycleId)
                    .OrderByDescending(o => o.Id)
                    .FirstOrDefaultAsync(ct);
                // 写工具被拒时周期是合法 completed + execution_errors（§6）：HTTP 仍是 200（周期确实跑了），
                // 但 order=null 必须带出周期错误码与可展示文案，UI 才能区分「已平仓」与「什么都没发生」。
                if (order is not null)
                {
                    return ApiResults.Ok(new ManualCloseResultDto(cycleId, CycleDtoMapper.ToOrder(order), null, null));
                }

                var cycle = await db.TradingCycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId, ct);
                return ApiResults.Ok(new ManualCloseResultDto(cycleId, null, cycle?.ErrorCode, cycle?.Error));
            });

            // 看板快照（§3.4 + 简报「最新周期/阶段/提案/裁决/订单/权益」）。
            // GET 无副作用：不再触发持仓采样与历史同步写库（原为每轮看板刷新手段，已移出读路径，见 task-14 报告）。
            group.MapGet("/{id:int}/dashboard", async (int id, AppDbContext db, ExchangeTradingService trading,
                CancellationToken ct) =>
            {
                var trader = await db.Traders.FindAsync(new object[] { id }, ct);
                if (trader is null) return ApiResults.Fail("not_found", "交易员不存在", 404);

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
                    CycleDtoMapper.Num(trader.InitialBalance),
                    trader.StartedAt,
                    trader.LastCycleAt,
                    trader.LastCycleStatus,
                    audit.CanTrade,
                    audit.Notes,
                    positionMode);

                // 账户与持仓（真实；只读，读取失败如实返回错误）
                DashboardAccount? accountDto = null;
                IReadOnlyList<DashboardPosition> positions = [];
                if (account is not null)
                {
                    var snapshot = await trading.GetAccountAsync(account, ct);
                    var livePositions = await trading.GetPositionsAsync(account, ct);
                    positions = livePositions
                        .Select(p => new DashboardPosition(p.Symbol, p.Side.ToString(),
                            CycleDtoMapper.Num(p.Quantity), CycleDtoMapper.Num(p.AverageOpenPrice),
                            CycleDtoMapper.Num(p.UnrealizedPnl), CycleDtoMapper.Num(p.LiquidationPrice),
                            CycleDtoMapper.Num(p.Leverage)))
                        .ToList();

                    decimal? pnl = null;
                    double? pnlPct = null;
                    if (snapshot.Success && trader.InitialBalance is { } initial && initial > 0)
                    {
                        pnl = Math.Round(snapshot.UsdtEquity - initial, 2);
                        pnlPct = Math.Round((double)((snapshot.UsdtEquity - initial) / initial * 100m), 2);
                    }

                    // 保证金占用 = Σ(数量 × 均价 / 杠杆) / 净值（用交易所口径的原始持仓，不解析字符串 DTO）
                    decimal? marginPct = null;
                    if (snapshot.Success && snapshot.UsdtEquity > 0)
                    {
                        var used = livePositions
                            .Where(p => p.Leverage is > 0)
                            .Sum(p => p.Quantity * p.AverageOpenPrice / p.Leverage!.Value);
                        marginPct = Math.Round(used / snapshot.UsdtEquity * 100m, 2);
                    }

                    accountDto = new DashboardAccount(
                        snapshot.Success, snapshot.Error,
                        snapshot.Success ? CycleDtoMapper.Num(snapshot.UsdtEquity) : null,
                        snapshot.Success ? CycleDtoMapper.Num(snapshot.UsdtAvailable) : null,
                        CycleDtoMapper.Num(pnl), CycleDtoMapper.Num(pnlPct),
                        positions.Count,
                        CycleDtoMapper.Num(marginPct),
                        DateTime.UtcNow);
                }

                // 净值样本（账户级，最近 200 个，时间升序供绘图；由同步宿主每约 60 秒采样）
                IReadOnlyList<DashboardSample> samples = account is null
                    ? []
                    : (await db.AccountEquitySamples
                            .Where(s => s.ExchangeAccountId == account.Id)
                            .OrderByDescending(s => s.Id)
                            .Take(200)
                            .ToListAsync(ct))
                        .OrderBy(s => s.At)
                        .Select(s => new DashboardSample(s.At, CycleDtoMapper.Num(s.Equity)))
                        .ToList();

                // 最近决策（旧 DecisionRecord 链路，P7 随旧宿主一并移除；新链数据在 latestCycle）
                var decisions = await LoadDecisionsAsync(db, id, 50, ct);

                // 历史仓位只读本系统的库；不再在 GET 路径触发的交易所同步（syncError 恒 null 的口径见上）。
                var history = account is null
                    ? EmptyHistory("绑定账户缺失")
                    : await BuildHistoryAsync(db, account, trader.InitialBalance, null, ct);

                // 历史仓位「杠杆」回填：Binance 等对已平仓位不提供杠杆，用持仓期间采样值补（仅补空值，只读）
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
                                return hit is null ? r : r with { Leverage = CycleDtoMapper.Num(hit.Leverage) };
                            })
                            .ToList();
                        var leverageNote = "杠杆取自持仓期间采样（每轮运行采样）";
                        history = history with
                        {
                            Rows = updatedRows,
                            Note = history.Note is null ? leverageNote : $"{history.Note}；{leverageNote}",
                        };
                    }
                }

                var orders = (await db.TraderOrders
                        .Where(o => o.TraderId == id)
                        .OrderByDescending(o => o.Id)
                        .Take(20)
                        .ToListAsync(ct))
                    .Select(ToOrderDto)
                    .ToList();

                // 新链路最新周期块（简报：dashboard 返回最新周期/阶段/提案/裁决/订单/权益）。
                var latestCycle = await CycleDtoMapper.BuildLatestAsync(db, id, ct);

                return ApiResults.Ok(new TraderDashboardDto(info, accountDto, samples, positions, decisions,
                    history, orders, latestCycle));
            });

            // 实时推送（SSE，LC-STD-03 §4.2 事件集）：模块落库后经 SseEventHub 发布，连接只订阅转发、
            // 不做轮询、绝不触发周期（GET 无副作用）。心跳 ping 每 15 秒（§4.1）。
            group.MapGet("/{id:int}/stream", async (int id, AppDbContext db, AccountStreamService stream,
                SseEventHub hub, HttpContext http, CancellationToken ct) =>
            {
                var trader = await db.Traders.FindAsync(new object[] { id }, ct);
                if (trader is null) return ApiResults.Fail("not_found", "交易员不存在", 404);
                var account = await db.ExchangeAccounts.FindAsync(new object[] { trader.ExchangeAccountId }, ct);

                http.Response.Headers.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["X-Accel-Buffering"] = "no";

                // 先订阅再播种：订阅之后产生的事件进入通道不丢失。
                var (traderEvents, traderSubscription) = hub.Subscribe(SseChannels.Trader(id));
                ChannelReader<SseEvent>? accountEvents = null;
                IDisposable? accountSubscription = null;
                if (account is not null)
                {
                    (accountEvents, accountSubscription) = hub.Subscribe(SseChannels.Account(account.Id));
                    await stream.AcquireAsync(account, ct); // 账户实时只读订阅（引用计数），不写业务数据
                }

                try
                {
                    // state 种子帧（§4.2 触发列「连接建立」；API-10：lastCycleStatus 约定枚举或 null）。
                    await WriteFrameAsync(http, "state",
                        new TraderStateEvent(trader.IsRunning, trader.LastCycleStatus,
                            CycleDtoMapper.Utc(trader.LastCycleAt)), ct);

                    var lastVersion = -1;
                    var lastWriteAt = Environment.TickCount64;
                    while (!ct.IsCancellationRequested)
                    {
                        // 事件帧：即时转发（巡检节拍 1 秒，事件送达延迟上限 1 秒）
                        while (traderEvents.TryRead(out var evt) || (accountEvents?.TryRead(out evt) ?? false))
                        {
                            if (ToEventFrame(evt) is not { } frame) continue;
                            await http.Response.WriteAsync($"event: {frame.Name}\ndata: {frame.Json}\n\n", ct);
                            await http.Response.Body.FlushAsync(ct);
                            lastWriteAt = Environment.TickCount64;
                        }

                        // 账户实时状态 → equity 帧（§4.2 只约定 { equity, at }；持仓明细走 dashboard 拉取）
                        if (account is not null)
                        {
                            var state = stream.GetState(account.Id);
                            if (state is { Equity: not null } snapshot && snapshot.Version != lastVersion)
                            {
                                lastVersion = snapshot.Version;
                                await WriteFrameAsync(http, "equity", new
                                {
                                    equity = CycleDtoMapper.Num(snapshot.Equity),
                                    at = CycleDtoMapper.Utc(snapshot.At),
                                }, ct);
                                lastWriteAt = Environment.TickCount64;
                            }
                        }

                        // 心跳帧：每 15 秒（静默期）写一帧轻量事件——防代理空闲超时掐断长连接，客户端据此判活
                        if (Environment.TickCount64 - lastWriteAt >= 15000)
                        {
                            await WriteFrameAsync(http, "ping", new { at = DateTime.UtcNow }, ct);
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
                    accountSubscription?.Dispose();
                    if (account is not null) stream.Release(account.Id);
                }

                return Results.Empty;
            });

            // 周期列表（分页 §1.3；周期快照字段不外泄 §2.1）
            group.MapGet("/{id:int}/cycles", async (int id, string? page, string? pageSize,
                AppDbContext db, CancellationToken ct) =>
            {
                if (!await db.Traders.AnyAsync(t => t.Id == id, ct))
                {
                    return ApiResults.Fail("not_found", "交易员不存在", 404);
                }

                if (!TryPagination(page, pageSize, out var pageNumber, out var size, out var error))
                {
                    return ApiResults.Fail("validation_failed", error!, 400);
                }

                var query = db.TradingCycles.AsNoTracking().Where(c => c.TraderId == id);
                var total = await query.CountAsync(ct);
                var rows = await query
                    .OrderByDescending(c => c.StartedAt)
                    .ThenByDescending(c => c.Id)
                    .Skip((pageNumber - 1) * size)
                    .Take(size)
                    .ToListAsync(ct);
                return ApiResults.Ok(new PagedResult<CycleInfoDto>(
                    rows.Select(CycleDtoMapper.ToInfo).ToList(), total, pageNumber, size));
            });

            // 复盘列表：持久 reviewer 阶段行（skipped/failed/completed 都留痕，复盘页数据源）
            group.MapGet("/{id:int}/reviews", async (int id, string? page, string? pageSize,
                AppDbContext db, CancellationToken ct) =>
            {
                if (!await db.Traders.AnyAsync(t => t.Id == id, ct))
                {
                    return ApiResults.Fail("not_found", "交易员不存在", 404);
                }

                if (!TryPagination(page, pageSize, out var pageNumber, out var size, out var error))
                {
                    return ApiResults.Fail("validation_failed", error!, 400);
                }

                var query = db.AgentStageRuns.AsNoTracking()
                    .Where(s => s.TraderId == id && s.Role == ToolRegistry.ReviewerRoleName);
                var total = await query.CountAsync(ct);
                var rows = await query
                    .OrderByDescending(s => s.CreatedAt)
                    .ThenByDescending(s => s.Id)
                    .Skip((pageNumber - 1) * size)
                    .Take(size)
                    .ToListAsync(ct);
                return ApiResults.Ok(new PagedResult<ReviewListItemDto>(
                    rows.Select(s => new ReviewListItemDto(s.Id, s.Status, s.Attempt, s.ProviderName, s.Model,
                        s.ElapsedMs, s.InputTokens, s.OutputTokens, s.ToolCallCount, s.Error,
                        CycleDtoMapper.Utc(s.CreatedAt))).ToList(),
                    total, pageNumber, size));
            });

            // 手动触发复盘（§9 触发行「本地时刻 + 手动端点」）。
            // R-14-6：60 秒重复触发闸在本端点侧完成——ReviewRunner 的进行中拒绝只覆盖「同时并发」，
            // 而契约（LC-STD-03 §1.4/§6）语义是「60 秒内重复触发 → 409」，判据必须是持久证据：
            // 最近 60 秒内的 reviewer 阶段行（含 skipped/failed 留痕）。不改 ReviewRunner 的进行中语义。
            group.MapPost("/{id:int}/reviews/run", async (int id, AppDbContext db, ReviewRunner reviewRunner,
                CancellationToken ct) =>
            {
                var trader = await db.Traders.FindAsync(new object[] { id }, ct);
                if (trader is null) return ApiResults.Fail("not_found", "交易员不存在", 404);

                var cooldownStart = DateTime.UtcNow.AddSeconds(-ReviewTriggerCooldownSeconds);
                if (await db.AgentStageRuns.AsNoTracking().AnyAsync(s =>
                        s.TraderId == id && s.Role == ToolRegistry.ReviewerRoleName && s.CreatedAt > cooldownStart, ct))
                {
                    return ApiResults.Fail("review_in_progress",
                        $"{ReviewTriggerCooldownSeconds} 秒内已触发过复盘，请稍后再试", 409);
                }

                var result = await reviewRunner.RunAsync(trader, ct);
                return result.Status switch
                {
                    ReviewRunner.InProgressStatus => ApiResults.Fail("review_in_progress",
                        "该交易员的复盘正在进行，请稍后再试", 409),
                    ReviewRunner.SkippedStatus when result.ErrorCode == ReviewRunner.NoDataCode =>
                        ApiResults.Fail(ReviewRunner.NoDataCode, "复盘窗口内无已平仓交易且无周期，本轮未产出经验", 422),
                    // completed / failed：触发本身成功，执行结果原样上报（status 区分；失败留痕不伪装成 HTTP 错误）。
                    _ => ApiResults.Ok(new ReviewRunDto(result.Status, result.ErrorCode, result.StageRunId,
                        result.ElapsedMs, CycleDtoMapper.Utc(result.PeriodStartUtc),
                        CycleDtoMapper.Utc(result.PeriodEndUtc))),
                };
            });

            // 经验列表（分页；正文 JSON 数组解析为列表，§2.3）
            group.MapGet("/{id:int}/lessons", async (int id, string? page, string? pageSize,
                AppDbContext db, CancellationToken ct) =>
            {
                if (!await db.Traders.AnyAsync(t => t.Id == id, ct))
                {
                    return ApiResults.Fail("not_found", "交易员不存在", 404);
                }

                if (!TryPagination(page, pageSize, out var pageNumber, out var size, out var error))
                {
                    return ApiResults.Fail("validation_failed", error!, 400);
                }

                var query = db.TraderLessons.AsNoTracking().Where(l => l.TraderId == id);
                var total = await query.CountAsync(ct);
                var rows = await query
                    .OrderByDescending(l => l.CreatedAt)
                    .ThenByDescending(l => l.Id)
                    .Skip((pageNumber - 1) * size)
                    .Take(size)
                    .ToListAsync(ct);
                return ApiResults.Ok(new PagedResult<LessonDto>(
                    rows.Select(l => new LessonDto(l.Id, l.TraderId, l.StageRunId,
                        CycleDtoMapper.Utc(l.PeriodStart), CycleDtoMapper.Utc(l.PeriodEnd), l.Summary,
                        CycleDtoMapper.ReadStringArray(l.StrengthsJson),
                        CycleDtoMapper.ReadStringArray(l.MistakesJson),
                        CycleDtoMapper.ReadStringArray(l.RulesJson),
                        CycleDtoMapper.ReadStringArray(l.EvidenceJson),
                        CycleDtoMapper.Utc(l.CreatedAt))).ToList(),
                    total, pageNumber, size));
            });

            return group;
        }
    }

    // —— 内部辅助 ——

    /// <summary>分页参数（§1.3）：page 从 1 起；pageSize 默认 20、最大 100；pageSize=0 与越界一律 400。</summary>
    private static bool TryPagination(string? page, string? pageSize, out int pageNumber, out int size,
        out string? error)
    {
        pageNumber = 1;
        size = 20;
        error = null;
        if (page is not null &&
            (!int.TryParse(page, NumberStyles.Integer, CultureInfo.InvariantCulture, out pageNumber)
             || pageNumber < 1))
        {
            error = "page 必须是从 1 起的整数";
            return false;
        }

        if (pageSize is not null &&
            (!int.TryParse(pageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
             || size < 1 || size > 100))
        {
            error = "pageSize 必须在 1 到 100 之间";
            return false;
        }

        return true;
    }

    /// <summary>手动平仓请求的形状校验（金额解析在 runner 内先于建行，FormatError 映射 400，R-14-1）。</summary>
    private static string? ValidateManualClose(ManualCloseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol)) return "symbol 不能为空";
        if (request.OrderType is not ("market" or "limit")) return "orderType 仅支持 market / limit";
        // §3.4「price 仅限价单必填」：限价单缺价格在创建任何行之前拒绝（R-14-1 同一形状）。
        if (request.OrderType == "limit" && string.IsNullOrWhiteSpace(request.Price))
            return "price 是限价单必填字段";
        if (string.IsNullOrWhiteSpace(request.Reason)) return "reason 不能为空";
        if (request.Reason.Length > 300) return "reason 不得超过 300 字";
        return null;
    }

    private static SseEvent StateEvent(Trader trader) =>
        new("state", new TraderStateEvent(trader.IsRunning, trader.LastCycleStatus,
            CycleDtoMapper.Utc(trader.LastCycleAt)));

    private static Task WriteFrameAsync(HttpContext http, string name, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, FrameJson);
        return http.Response.WriteAsync($"event: {name}\ndata: {json}\n\n", ct);
    }

    // SSE 帧序列化与看板 HTTP 载荷**同构**：不省略 null 字段——前端按 `=== null` 判空（盈亏比/信心度等），
    // 字段缺失会让判空失效（undefined 绕过判空，曾致决策卡显示「1 : NaN」）
    private static readonly JsonSerializerOptions FrameJson = new(JsonSerializerDefaults.Web);

    // 总线事件 → SSE 帧（§4.2 事件集；未知事件忽略，便于后续扩展新事件类型）。
    // 试运行事件不发往交易员频道（R-11-7：按周期独立 test-run 频道），这里天然收不到、也不外泄。
    private static (string Name, string Json)? ToEventFrame(SseEvent evt) => evt switch
    {
        { Name: "state", Payload: TraderStateEvent s } =>
            ("state", JsonSerializer.Serialize(s, FrameJson)),
        { Name: "cycle", Payload: CycleStartedEvent or CycleCompletedEvent } =>
            ("cycle", JsonSerializer.Serialize(evt.Payload!, FrameJson)),
        { Name: "stage", Payload: StageRecordedEvent stage } =>
            ("stage", JsonSerializer.Serialize(stage, FrameJson)),
        { Name: "proposal", Payload: TradeProposalDto proposal } =>
            ("proposal", JsonSerializer.Serialize(proposal, FrameJson)),
        { Name: "verdict", Payload: VerdictRecordedEvent verdict } =>
            ("verdict", JsonSerializer.Serialize(verdict, FrameJson)),
        { Name: "tool" } =>
            ("tool", JsonSerializer.Serialize(evt.Payload!, FrameJson)),
        { Name: "order", Payload: TraderOrder order } =>
            ("order", JsonSerializer.Serialize(CycleDtoMapper.ToOrder(order), FrameJson)),
        { Name: "equity", Payload: AccountEquitySample sample } =>
            ("equity", JsonSerializer.Serialize(new
            {
                equity = CycleDtoMapper.Num(sample.Equity),
                at = CycleDtoMapper.Utc(sample.At),
            }, FrameJson)),
        { Name: "position-history", Payload: PositionHistoryEventDto history } =>
            ("position-history", JsonSerializer.Serialize(history, FrameJson)),
        _ => null,
    };

    // —— 以下为旧看板聚合的只读段（DecisionRecord 链路到 P7 移除；GET 内不写库、不触交易所同步）——

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
        return new DashboardDecision(r.Id, r.CreatedAt, r.Action, r.Symbol, CycleDtoMapper.Num(r.Confidence),
            CycleDtoMapper.Num(entry), CycleDtoMapper.Num(sl), CycleDtoMapper.Num(tp),
            CycleDtoMapper.Num(posPct), CycleDtoMapper.Num(lev), reason, r.Reasoning,
            r.InputTokens, r.OutputTokens, r.ElapsedMs, r.TraderId,
            r.SystemPrompt, r.UserPrompt, round?.Status ?? (r.Action == "error" ? "error" : "ok"),
            round?.FailureStage, round?.Error, round?.RawOutput, round?.PromptRevision, round?.RequestSent);
    }

    private static TraderOrderDto ToOrderDto(TraderOrder o) =>
        new(o.Id, o.At, o.Symbol, o.Action, CycleDtoMapper.Num(o.Quantity), CycleDtoMapper.Num(o.Price),
            o.Note, o.Error);

    // 历史仓位（自维护库读取：PositionHistories 由同步宿主从交易所成交明细重建/共享接口增量落库）
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

        // 时间升序供回撤/夏普计算（直接基于落库实体，DTO 出参统一转字符串）
        var asc = records.OrderBy(r => r.CloseTime).ToList();
        var wins = asc.Where(r => r.RealizedPnl > 0).ToList();
        var losses = asc.Where(r => r.RealizedPnl < 0).ToList();
        var totalPnl = asc.Sum(r => r.RealizedPnl);
        var totalFee = asc.Sum(r => r.Fee ?? 0m);
        var winSum = wins.Sum(r => r.RealizedPnl);
        var lossSum = Math.Abs(losses.Sum(r => r.RealizedPnl));

        var metrics = new DashboardMetrics(
            asc.Count,
            CycleDtoMapper.Num(asc.Count > 0 ? Math.Round((double)wins.Count / asc.Count * 100, 2) : 0),
            CycleDtoMapper.Num(Math.Round(totalPnl, 4)),
            CycleDtoMapper.Num(Math.Round(totalFee, 4)),
            CycleDtoMapper.Num(Math.Round(totalPnl - totalFee, 4)),
            CycleDtoMapper.Num(lossSum > 0 ? Math.Round(winSum / lossSum, 2) : (winSum > 0 ? 999m : 0m)),
            CycleDtoMapper.Num(wins.Count > 0 ? Math.Round(winSum / wins.Count, 4) : 0m),
            CycleDtoMapper.Num(losses.Count > 0 ? Math.Round(losses.Sum(r => r.RealizedPnl) / losses.Count, 4) : 0m),
            CycleDtoMapper.Num(ComputeMaxDrawdownPct(asc, initialBalance)),
            CycleDtoMapper.Num(ComputeSharpe(asc)));

        var directions = asc
            .GroupBy(r => r.Side ?? "", StringComparer.Ordinal)
            .Select(g => new DashboardDirectionStat(
                g.Key,
                g.Count(),
                CycleDtoMapper.Num(Math.Round((double)g.Count(r => r.RealizedPnl > 0) / g.Count() * 100, 2)),
                CycleDtoMapper.Num(Math.Round(g.Sum(r => r.RealizedPnl), 4)),
                CycleDtoMapper.Num(Math.Round(g.Average(r => r.RealizedPnl), 4))))
            .ToList();

        var symbols = asc
            .GroupBy(r => r.Symbol, StringComparer.Ordinal)
            .Select(g => new DashboardSymbolStat(
                g.Key,
                g.Count(),
                CycleDtoMapper.Num(Math.Round((double)g.Count(r => r.RealizedPnl > 0) / g.Count() * 100, 2)),
                CycleDtoMapper.Num(Math.Round(g.Sum(r => r.RealizedPnl), 4))))
            .OrderByDescending(s => s.Count)
            .ToList();

        // 来源口径说明（按落库行来源合成）
        var sources = records.Select(p => p.Source).Distinct(StringComparer.Ordinal).ToList();
        var note = sources.Contains("binance-trades", StringComparer.Ordinal)
            ? "数据来源：本系统维护的仓位历史（同步自交易所成交明细 FIFO 重建；未平仓位不计入；非 USDT 计价手续费未计入）"
            : "数据来源：本系统维护的仓位历史（同步自交易所共享接口持仓历史）";

        // 展示按时间倒序；金额/数量一律字符串（§1.3）
        var rows = asc.OrderByDescending(r => r.CloseTime)
            .Select(p => new DashboardHistoryRow(
                p.Symbol,
                p.Side,
                CycleDtoMapper.Num(p.EntryPrice),
                CycleDtoMapper.Num(p.ExitPrice),
                CycleDtoMapper.Num(p.Quantity),
                CycleDtoMapper.Num(p.Leverage),
                CycleDtoMapper.Num(p.RealizedPnl),
                CycleDtoMapper.Num(p.Fee),
                p.CloseTime))
            .ToList();
        return new DashboardHistory(true, null, metrics, directions, symbols, rows, note);
    }

    private static DashboardHistory EmptyHistory(string? error, string? note = null) =>
        new(false, error,
            new DashboardMetrics(0, "0", "0", "0", "0", "0", "0", "0", "0", "0"),
            [], [], [], note);

    // 回撤：已实现盈亏累计曲线的峰值回撤；百分比相对初始余额（缺失时回退为峰值绝对值）——
    // 直接以峰值为分母在峰值很小时会得出无意义的百分比
    private static double ComputeMaxDrawdownPct(IReadOnlyList<PositionHistory> asc, decimal? initialBalance)
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
    private static double ComputeSharpe(IReadOnlyList<PositionHistory> asc)
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

    private static async Task<string?> ValidateAsync(TraderUpsertRequest request, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "名称不能为空";
        if (!await db.Providers.AnyAsync(p => p.Id == request.ProviderId, ct)) return "选择的模型提供商不存在";
        if (!await db.ExchangeAccounts.AnyAsync(a => a.Id == request.ExchangeAccountId, ct)) return "选择的交易所账户不存在";
        if (!await db.Strategies.AnyAsync(s => s.Id == request.StrategyId, ct)) return "选择的策略不存在";
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
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
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
