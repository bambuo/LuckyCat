using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CryptoExchange.Net.SharedApis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Endpoints;
using LuckyCat.Models;
using LuckyCat.Services;
using LuckyCat.Workflows;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LuckyCat.Tests;

/// <summary>
/// Task 14 B 组 HTTP 契约测试（LC-STD-05 §4.3 API-01~10 中 B 组覆盖面）：
/// traders 面（start/stop 能力闸与停启语义、close-position 的 409/400 无行、reviews/run 的
/// 60 秒端点侧闸、cycles/lessons/reviews 分页、dashboard 无副作用包裹、stream 契约事件）、
/// cycles 面（周期详情 open 门控、阶段全文与截断标记、试运行周期可取回）与新版 test-run SSE。
/// 宿主按 ApiContractTests 模式手工装配：只映射端点、不注册任何 hosted service；
/// 交易循环走「脚本化假模型 + 假账户/行情/网关」的 TestRunner 子类，零真实网络与真实下单。
/// </summary>
public class CycleHttpTests
{
    // —— 启停（API-09 / LC-STD-01 §8.6）——

    [Fact]
    public async Task Start_without_tool_verification_returns_422_and_does_not_schedule()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, providerVerified: false);

        var response = await host.Client.PostAsync("/api/traders/1/start", null);
        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "provider_capability_missing");

        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False((await db.Traders.AsNoTracking().SingleAsync(t => t.Id == 1)).IsRunning);
        Assert.False(host.Runner.IsRunning(1));
        Assert.Equal(0, await db.TradingCycles.CountAsync());
    }

    [Fact]
    public async Task Start_when_verified_schedules_the_new_chain_and_stop_is_idempotent()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        var started = await host.Client.PostAsync("/api/traders/1/start", null);
        await AssertWrappedOkAsync(started);
        Assert.True((await ReadJsonAsync(started)).GetProperty("data").GetProperty("isRunning").GetBoolean());
        Assert.True(host.Runner.IsRunning(1));

        // 幂等：重复 start 仍 200，且只有一条调度循环（R-14-5 单调度器）。
        var again = await host.Client.PostAsync("/api/traders/1/start", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // 新链路立刻开首个周期（trigger=schedule），stop 之后不再有 running。
        await WaitUntilAsync(async () => await host.CountCyclesAsync() >= 1);
        var stopped = await host.Client.PostAsync("/api/traders/1/stop", null);
        await AssertWrappedOkAsync(stopped);
        Assert.False(host.Runner.IsRunning(1));

        var idempotentStop = await host.Client.PostAsync("/api/traders/1/stop", null);
        Assert.Equal(HttpStatusCode.OK, idempotentStop.StatusCode);

        await WaitUntilAsync(async () =>
        {
            using var scope = host.App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.TradingCycles.AsNoTracking().AllAsync(c => c.Status != "running");
        });
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cycle = await db.TradingCycles.AsNoTracking().SingleAsync();
            Assert.Equal("schedule", cycle.Trigger);
        }
    }

    [Fact]
    public async Task Stop_cancels_the_running_cycle_as_aborted_and_publishes_state()
    {
        await using var host = await TestApp.StartAsync(analystBehavior: "hang");
        await SeedAsync(host);
        var (reader, subscription) = host.Hub.Subscribe(SseChannels.Trader(1));
        using var _ = subscription;

        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync("/api/traders/1/start", null)).StatusCode);
        await WaitUntilAsync(async () => await host.HasRunningCycleAsync());

        var stop = await host.Client.PostAsync("/api/traders/1/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        await WaitUntilAsync(async () =>
        {
            using var scope = host.App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cycle = await db.TradingCycles.AsNoTracking().SingleAsync();
            return cycle.Status == "aborted" && cycle.ErrorCode == "cancelled_by_user";
        });

        var events = Drain(reader);
        // 启停各发一个 state 帧；周期收敛的终态 cycle 帧是异步的，只断言最后一个 state。
        var states = events.Where(e => e.Name == "state").ToList();
        Assert.True(states.Count >= 1);
        var stateJson = Serialize(states[^1]);
        Assert.False(stateJson.GetProperty("isRunning").GetBoolean());
        // API-10：state 帧的 lastCycleStatus 只取约定枚举或 null。
        var lastStatus = stateJson.GetProperty("lastCycleStatus");
        Assert.True(lastStatus.ValueKind is JsonValueKind.Null
            || new[] { "running", "completed", "failed", "skipped", "aborted" }.Contains(lastStatus.GetString()));
    }

    // —— 手动平仓（R-14-1 / R-14-7）——

    [Fact]
    public async Task Close_position_creates_manual_cycle_and_returns_wrapped_order()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        var response = await host.Client.PostAsJsonAsync("/api/traders/1/close-position",
            new { symbol = "BTC/USDT", orderType = "market", reason = "手动减仓" }, ApiJson.Options);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        var cycleId = body.GetProperty("data").GetProperty("cycleId").GetString();
        Assert.False(string.IsNullOrEmpty(cycleId));
        var order = body.GetProperty("data").GetProperty("order");
        Assert.Equal("filled", order.GetProperty("status").GetString());
        Assert.True(order.GetProperty("simulated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").GetProperty("errorCode").ValueKind);
        // 金额/数量一律字符串（§1.1）。
        Assert.Equal(JsonValueKind.String, order.GetProperty("quantity").ValueKind);
        Assert.Equal(0, host.Gateway.CloseCallCount); // 演练平仓不触网关（API-06 同口径）

        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cycle = await db.TradingCycles.AsNoTracking().SingleAsync(c => c.Id == cycleId);
        Assert.Equal("manual", cycle.Trigger);
        Assert.Equal(1, cycle.TraderId);
    }

    [Fact]
    public async Task Close_position_with_malformed_quantity_returns_400_before_any_row()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        var response = await host.Client.PostAsJsonAsync("/api/traders/1/close-position",
            new { symbol = "BTC/USDT", quantity = "not-a-number", orderType = "market", reason = "手动减仓" },
            ApiJson.Options);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(0, await host.CountCyclesAsync()); // 不落任何周期行（R-14-1）
    }

    [Fact]
    public async Task Close_position_limit_without_price_returns_400_before_any_row()
    {
        // §3.4「price 仅限价单必填」：缺 price 的限价单在创建任何周期/订单行之前拒绝。
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        var response = await host.Client.PostAsJsonAsync("/api/traders/1/close-position",
            new { symbol = "BTC/USDT", orderType = "limit", reason = "手动减仓" }, ApiJson.Options);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed", "price");
        Assert.Equal(0, await host.CountCyclesAsync());
    }

    [Fact]
    public async Task Close_position_rejection_returns_200_with_cycle_error_and_no_order()
    {
        // 写工具拒绝（无该标的持仓）时周期是合法 completed + execution_errors（§6），
        // 响应必须带出错误码与可展示文案，且 order 为 null——绝不能暗示成交。
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        var response = await host.Client.PostAsJsonAsync("/api/traders/1/close-position",
            new { symbol = "ETH/USDT", orderType = "market", reason = "手动减仓" }, ApiJson.Options);
        var body = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());
        var data = body.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("order").ValueKind);
        Assert.Equal("execution_errors", data.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("error").GetString()));

        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.TraderOrders.CountAsync());
        var cycle = await db.TradingCycles.AsNoTracking().SingleAsync();
        Assert.Equal("completed", cycle.Status);
        Assert.Equal("execution_errors", cycle.ErrorCode);
    }

    [Fact]
    public async Task Close_position_with_missing_bound_account_returns_400_validation_failed()
    {
        // 前置条件（绑定账户行已不存在）是请求可修复的问题，不是 500；且不得留下任何周期行。
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, danglingAccount: true);

        var response = await host.Client.PostAsJsonAsync("/api/traders/2/close-position",
            new { symbol = "BTC/USDT", orderType = "market", reason = "手动减仓" }, ApiJson.Options);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed", "交易所账户");
        Assert.Equal(0, await host.CountCyclesAsync());
    }

    [Fact]
    public async Task Close_position_while_a_cycle_runs_returns_409_without_a_new_row()
    {
        // R-14-7：CycleInProgressException → 409 契约形状，不创建周期行。
        await using var host = await TestApp.StartAsync(analystBehavior: "hang");
        await SeedAsync(host);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = Task.Run(async () =>
        {
            using var scope = host.App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trader = await db.Traders.SingleAsync(t => t.Id == 1);
            await host.Runner.RunAsync(trader, cts.Token);
        });
        await WaitUntilAsync(async () => await host.HasRunningCycleAsync());

        var response = await host.Client.PostAsJsonAsync("/api/traders/1/close-position",
            new { symbol = "BTC/USDT", orderType = "market", reason = "手动减仓" }, ApiJson.Options);
        await AssertErrorAsync(response, HttpStatusCode.Conflict, "cycle_in_progress");
        Assert.Equal(1, await host.CountCyclesAsync()); // 被拒的平仓没有落行

        cts.Cancel();
        await running;
    }

    // —— 复盘触发（R-14-6：端点侧 60 秒闸，持久 reviewer 阶段行为证据）——

    [Fact]
    public async Task Reviews_run_within_sixty_seconds_returns_409_without_calling_the_model()
    {
        await using var host = await TestApp.StartAsync(closedTrades: 1);
        await SeedAsync(host);
        await SeedReviewStageAsync(host, "completed", DateTime.UtcNow.AddSeconds(-30));
        var callsBefore = host.Chat.Calls;

        var response = await host.Client.PostAsync("/api/traders/1/reviews/run", null);
        await AssertErrorAsync(response, HttpStatusCode.Conflict, "review_in_progress");
        Assert.Equal(callsBefore, host.Chat.Calls); // 端点先拦，ReviewRunner 完全没跑
        Assert.Equal(1, await CountReviewStagesAsync(host)); // 没有新阶段行
    }

    [Fact]
    public async Task Reviews_run_after_sixty_seconds_completes_then_next_trigger_is_gated()
    {
        await using var host = await TestApp.StartAsync(closedTrades: 1);
        await SeedAsync(host);
        await SeedReviewStageAsync(host, "completed", DateTime.UtcNow.AddSeconds(-90));

        var response = await host.Client.PostAsync("/api/traders/1/reviews/run", null);
        var body = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("completed", body.GetProperty("data").GetProperty("status").GetString());
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.TraderLessons.ToListAsync());
        }

        // 刚完成复盘的行立刻把下一次触发挡在 60 秒闸外（端点侧检查）。
        var second = await host.Client.PostAsync("/api/traders/1/reviews/run", null);
        await AssertErrorAsync(second, HttpStatusCode.Conflict, "review_in_progress");
    }

    [Fact]
    public async Task Reviews_run_without_window_data_returns_422_and_the_skip_row_gates_the_next_trigger()
    {
        await using var host = await TestApp.StartAsync(); // closedTrades=0 → 跳过路径
        await SeedAsync(host);

        var response = await host.Client.PostAsync("/api/traders/1/reviews/run", null);
        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "review_no_data");
        Assert.Equal(1, await CountReviewStagesAsync(host)); // skipped 阶段行留痕（R-13-3）

        var second = await host.Client.PostAsync("/api/traders/1/reviews/run", null);
        await AssertErrorAsync(second, HttpStatusCode.Conflict, "review_in_progress");
    }

    [Fact]
    public async Task Reviews_list_returns_only_reviewer_stage_rows()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);
        await SeedReviewStageAsync(host, "completed", DateTime.UtcNow.AddMinutes(-5));
        await SeedReviewStageAsync(host, "failed", DateTime.UtcNow.AddMinutes(-2));
        await SeedForeignStageAsync(host); // 非 reviewer 角色不得混入

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/traders/1/reviews?page=1&pageSize=10"));
        var data = body.GetProperty("data");
        Assert.Equal(2, data.GetProperty("total").GetInt32());
        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("failed", items[0].GetProperty("status").GetString()); // 时间倒序
        Assert.StartsWith("stage_failed", items[0].GetProperty("error").GetString());
    }

    // —— 分页（API-03/04/10：约定形状与三语义）——

    [Fact]
    public async Task Cycles_list_pagination_uses_the_conventional_envelope_and_rejects_invalid_pagesize()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        // 空结果：items [] + total 0，不得为 null（§1.1/§1.3）。
        var empty = await ReadJsonAsync(await host.Client.GetAsync("/api/traders/1/cycles"));
        var emptyData = empty.GetProperty("data");
        Assert.Equal(0, emptyData.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Array, emptyData.GetProperty("items").ValueKind);
        Assert.Empty(emptyData.GetProperty("items").EnumerateArray());
        Assert.Equal(1, emptyData.GetProperty("page").GetInt32());
        Assert.Equal(20, emptyData.GetProperty("pageSize").GetInt32());

        await SeedCyclesAsync(host, 3);
        var paged = await ReadJsonAsync(await host.Client.GetAsync("/api/traders/1/cycles?page=1&pageSize=2"));
        Assert.Equal(3, paged.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(2, paged.GetProperty("data").GetProperty("items").GetArrayLength());
        // 配置快照与指纹只存库不出现在列表（LC-STD-03 §2.1）。
        var raw = paged.ToString();
        Assert.DoesNotContain("strategyConfigJson", raw);
        Assert.DoesNotContain("memoryFingerprint", raw);

        foreach (var bad in new[] { "pageSize=0", "pageSize=101", "page=abc", "page=0", "page=10001",
                     "page=2147483647&pageSize=100" })
        {
            await AssertErrorAsync(await host.Client.GetAsync($"/api/traders/1/cycles?{bad}"),
                HttpStatusCode.BadRequest, "validation_failed");
        }

        await AssertErrorAsync(await host.Client.GetAsync("/api/traders/999/cycles"),
            HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Lessons_list_returns_parsed_arrays_and_paginates()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);
        await SeedLessonAsync(host);

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/traders/1/lessons"));
        var item = body.GetProperty("data").GetProperty("items")[0];
        Assert.Equal("复盘结论：守住止损就是纪律", item.GetProperty("summary").GetString());
        Assert.Equal(["规则一", "规则二"], item.GetProperty("rules").EnumerateArray().Select(v => v.GetString()));

        await AssertErrorAsync(await host.Client.GetAsync("/api/traders/1/lessons?pageSize=500"),
            HttpStatusCode.BadRequest, "validation_failed");
    }

    // —— dashboard（API-10：包裹 + 最新周期块 + GET 无副作用）——

    [Fact]
    public async Task Dashboard_is_wrapped_exposes_latest_cycle_block_and_triggers_nothing()
    {
        // trader 2 绑定缺失账户：真实 ExchangeTradingService 已注册但绝不触达（GET 无交易所读、无写副作用）。
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, danglingAccount: true);
        await SeedOpenCycleAsync(host, traderId: 2);
        await SeedRecentOrderAsync(host, traderId: 2);

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/traders/2/dashboard"));
        var latest = body.GetProperty("data").GetProperty("latestCycle");
        Assert.Equal("01HTTPTESTCYCLE000000001", latest.GetProperty("cycle").GetProperty("id").GetString());
        Assert.Equal("approved", latest.GetProperty("verdict").GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.String,
            latest.GetProperty("orders")[0].GetProperty("quantity").ValueKind);

        // 看板自身的金额/数量/百分比同样是字符串（§1.3），订单不再携带已删除的 success（§2.2）。
        var data = body.GetProperty("data");
        Assert.Equal(JsonValueKind.String,
            data.GetProperty("trader").GetProperty("initialBalance").ValueKind);
        var metrics = data.GetProperty("history").GetProperty("metrics");
        Assert.Equal(JsonValueKind.String, metrics.GetProperty("totalPnl").ValueKind);
        Assert.Equal(JsonValueKind.String, metrics.GetProperty("winRate").ValueKind);
        var recent = data.GetProperty("recentOrders")[0];
        Assert.Equal(JsonValueKind.String, recent.GetProperty("quantity").ValueKind);
        Assert.Equal(JsonValueKind.String, recent.GetProperty("price").ValueKind);
        Assert.False(recent.TryGetProperty("success", out _));

        // GET 无副作用：dashboard 两次读取不新建周期、不改状态。
        Assert.Equal(1, await host.CountCyclesAsync());
        await host.Client.GetAsync("/api/traders/2/dashboard");
        Assert.Equal(1, await host.CountCyclesAsync());

        // API-10：看板回报的 lastCycleStatus 只取约定枚举或 null。
        var status = body.GetProperty("data").GetProperty("trader").GetProperty("lastCycleStatus");
        Assert.True(status.ValueKind is JsonValueKind.Null
            || new[] { "running", "completed", "failed", "skipped", "aborted" }.Contains(status.GetString()));
    }

    [Fact]
    public async Task Unknown_last_cycle_status_is_reported_as_null_in_dashboard_and_state_seed()
    {
        // API-10：lastCycleStatus 只取约定枚举或 null；库内脏值不得透传到看板/SSE。
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, danglingAccount: true);
        await SetLastCycleStatusAsync(host, 2, "bogus_status");

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/traders/2/dashboard"));
        Assert.Equal(JsonValueKind.Null,
            body.GetProperty("data").GetProperty("trader").GetProperty("lastCycleStatus").ValueKind);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var response = await host.Client.GetAsync("/api/traders/2/stream",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var frames = await ReadSseFramesAsync(stream, cts.Token, names => names.Contains("state"), minimum: 1);
        Assert.Equal(JsonValueKind.Null,
            frames.First(f => f.Name == "state").Data.GetProperty("lastCycleStatus").ValueKind);
        cts.Cancel();
        response.Dispose();
    }

    [Fact]
    public async Task Dashboard_and_stream_do_not_require_the_legacy_runner_and_traders_list_is_wrapped()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        var list = await ReadJsonAsync(await host.Client.GetAsync("/api/traders"));
        Assert.True(list.GetProperty("success").GetBoolean());
        Assert.Equal("脚本交易员", list.GetProperty("data")[0].GetProperty("name").GetString());
    }

    // —— stream（API-05：事件名/字段/心跳/空行帧；GET 无副作用）——

    [Fact]
    public async Task Stream_seeds_state_forwards_contracted_events_and_pings_without_side_effects()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, danglingAccount: true); // trader 2 → 账户 999：不触交易所 WS

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var response = await host.Client.GetAsync("/api/traders/2/stream",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        // 首帧（state 种子）送达说明端点已完成订阅；此时向总线注入一条已落库口径的 stage 事件。
        var frames = await ReadSseFramesAsync(stream, cts.Token,
            names => names.Contains("state") && names.Contains("stage") && names.Contains("ping"),
            minimum: 3,
            afterFirst: () => host.Hub.Publish(SseChannels.Trader(2), new SseEvent("stage",
                new StageRecordedEvent("01HTTPSTREAMCYCLE00000000", "01HTTPSTREAMSTAGE000000000",
                    "analyst", "completed", 1, 100, 10, 2, 3, "候选 1 个", null))));

        var state = frames.First(f => f.Name == "state");
        Assert.True(state.Data.TryGetProperty("isRunning", out _));
        Assert.True(state.Data.TryGetProperty("lastCycleStatus", out _));
        Assert.True(state.Data.TryGetProperty("lastCycleAt", out _));
        var stage = frames.First(f => f.Name == "stage");
        foreach (var field in new[] { "cycleId", "stageId", "role", "status", "attempt", "elapsedMs",
                     "inputTokens", "outputTokens", "toolCalls", "summary", "error" })
        {
            Assert.True(stage.Data.TryGetProperty(field, out _), $"stage 帧缺少约定字段 {field}");
        }

        // GET 无副作用：连流与推送期间不得新建周期。
        Assert.Equal(0, await host.CountCyclesAsync());
        cts.Cancel();
        response.Dispose();
    }

    [Fact]
    public void Event_frame_normalizes_order_payload_for_both_streams()
    {
        // test-run 与交易员 stream 共用 ToEventFrame：即使 order 载荷意外出现，也按 §4.2 契约出帧
        // （数量/价格为字符串、无 success）。
        var frame = TradersEndpoints.ToEventFrame(new SseEvent("order", new TraderOrder
        {
            Id = 9, TraderId = 1, At = DateTime.UtcNow, Action = "open", Symbol = "BTC/USDT",
            Quantity = 0.01m, Price = 100m, Status = "filled", Success = true,
        }));
        Assert.NotNull(frame);
        using var doc = JsonDocument.Parse(frame!.Value.Json);
        Assert.Equal("order", frame.Value.Name);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("quantity").ValueKind);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("price").ValueKind);
        Assert.False(doc.RootElement.TryGetProperty("success", out _));
    }

    [Fact]
    public async Task Stream_disposes_subscriptions_when_account_acquisition_throws()
    {
        // 账户装配抛错（未知交易所类型 → ExchangeClientFactory.Create 抛 ArgumentOutOfRangeException）：
        // AcquireAsync 之前创建的两个订阅必须在异常路径上同样释放，否则无界通道永久泄漏。
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);
        await SeedUnsupportedExchangeTraderAsync(host);

        var response = await host.Client.GetAsync("/api/traders/3/stream");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, SubscriberCount(host.Hub, SseChannels.Trader(3)));
        Assert.Equal(0, SubscriberCount(host.Hub, SseChannels.Account(3)));
    }

    /// <summary>增量读取 SSE 帧直到谓词满足（用于 stream 长连接测试）；首帧到达后执行一次 <paramref name="afterFirst"/>。</summary>
    private static async Task<List<SseFrame>> ReadSseFramesAsync(
        Stream stream, CancellationToken ct, Func<List<string>, bool> until, int minimum,
        Action? afterFirst = null)
    {
        var frames = new List<SseFrame>();
        var buffer = new byte[4096];
        var text = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(35);
        var injected = false;
        while (DateTime.UtcNow < deadline)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                var read = await stream.ReadAsync(buffer, readTimeout.Token);
                if (read == 0) break;
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("SSE 帧读取超时");
            }

            while (text.ToString().Contains("\n\n", StringComparison.Ordinal))
            {
                var raw = text.ToString();
                var cut = raw.IndexOf("\n\n", StringComparison.Ordinal) + 2;
                var block = raw[..cut];
                text = new StringBuilder(raw[cut..]);
                var parsed = ParseSse(block);
                frames.AddRange(parsed);
            }

            if (!injected && frames.Count > 0 && afterFirst is not null)
            {
                injected = true;
                afterFirst();
            }

            if (frames.Count >= minimum && until(frames.Select(f => f.Name).ToList())) return frames;
        }

        Assert.Fail($"SSE 在超时前未满足期望（已收到 {string.Join(",", frames.Select(f => f.Name))}）");
        return frames;
    }

    // —— 周期详情（R-14-9 / API-08）——

    [Fact]
    public async Task Cycle_detail_returns_stages_proposal_verdict_and_orders_with_string_amounts()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);
        await SeedOpenCycleAsync(host);

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/cycles/01HTTPTESTCYCLE000000001"));
        var data = body.GetProperty("data");
        Assert.Equal(2, data.GetProperty("stages").GetArrayLength());
        var proposal = data.GetProperty("proposal");
        Assert.Equal("open", proposal.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.String, proposal.GetProperty("entryPrice").ValueKind);
        Assert.Equal("100.25", proposal.GetProperty("entryPrice").GetString());
        Assert.Equal("approved", data.GetProperty("verdict").GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.String, data.GetProperty("verdict").GetProperty("maxLeverage").ValueKind);
        var order = data.GetProperty("orders")[0];
        Assert.Equal(JsonValueKind.String, order.GetProperty("quantity").ValueKind);
        // 周期快照不回显配置正文（§2.1）。
        Assert.DoesNotContain("strategyConfigJson", body.ToString());
    }

    [Fact]
    public async Task Cycle_detail_omits_verdict_when_the_proposal_is_not_open()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);
        await SeedHoldCycleAsync(host); // hold 提案 + 一条游离裁决行：端点必须按 open 门控为 null

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/cycles/01HTTPTESTCYCLE000000002"));
        Assert.Equal("hold", body.GetProperty("data").GetProperty("proposal").GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").GetProperty("verdict").ValueKind);
        Assert.Empty(body.GetProperty("data").GetProperty("orders").EnumerateArray());
    }

    [Fact]
    public async Task Cycle_detail_of_a_test_run_is_fetchable_without_any_trader_or_account_link()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);
        await SeedTestCycleAsync(host);

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/cycles/01HTTPTESTCYCLE000000003"));
        var cycle = body.GetProperty("data").GetProperty("cycle");
        Assert.Equal(JsonValueKind.Null, cycle.GetProperty("traderId").ValueKind);
        Assert.Equal(JsonValueKind.Null, cycle.GetProperty("exchangeAccountId").ValueKind);
        Assert.Equal("test", cycle.GetProperty("trigger").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").GetProperty("proposal").ValueKind);
    }

    [Fact]
    public async Task Cycle_detail_unknown_returns_wrapped_404()
    {
        await using var host = await TestApp.StartAsync();
        await AssertErrorAsync(await host.Client.GetAsync("/api/cycles/01NOSUCHCYCLE0000000000"),
            HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Stage_detail_returns_full_transcript_honors_truncation_and_sums_tools()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);
        await SeedOpenCycleAsync(host);

        var body = await ReadJsonAsync(await host.Client.GetAsync(
            "/api/cycles/01HTTPTESTCYCLE000000001/stages/01HTTPTESTSTAGE0000000001"));
        var data = body.GetProperty("data");
        Assert.Contains("已截断", data.GetProperty("transcriptJson").GetString());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("instructions").GetString()));
        var tool = data.GetProperty("toolCalls")[0];
        Assert.Equal("get_klines", tool.GetProperty("toolName").GetString());
        Assert.Equal("tool_budget_exceeded", tool.GetProperty("errorCode").GetString());

        // 阶段必须属于该周期：跨周期取回 404（不泄漏他人阶段）。
        await AssertErrorAsync(await host.Client.GetAsync(
            "/api/cycles/01HTTPTESTCYCLE000000002/stages/01HTTPTESTSTAGE0000000001"),
            HttpStatusCode.NotFound, "not_found");
    }

    // —— 新版 test-run（R-14-8 / API-06 / API-09）——

    [Fact]
    public async Task Test_run_returns_422_when_provider_capability_missing()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, providerVerified: false);

        var response = await host.Client.PostAsJsonAsync("/api/strategies/test-run",
            new { strategyId = 1, providerId = 1 }, ApiJson.Options);
        await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "provider_capability_missing");
        Assert.Equal(0, await host.CountCyclesAsync());
    }

    [Fact]
    public async Task Test_run_streams_the_contracted_events_never_trades_and_persists_a_test_cycle()
    {
        await using var host = await TestApp.StartAsync(executorBehavior: "order");
        await SeedAsync(host);

        var response = await host.Client.PostAsJsonAsync("/api/strategies/test-run",
            new { strategyId = 1, providerId = 1, includeMemory = false }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var sse = await response.Content.ReadAsStringAsync();

        var frames = ParseSse(sse);
        var names = frames.Select(f => f.Name).ToList();
        Assert.Equal("state", names[0]); // 连接建立即发 state（§4.2 触发列）
        Assert.Equal(2, names.Count(n => n == "cycle")); // 开始 + 终态
        Assert.Contains("stage", names);
        Assert.Contains("proposal", names);
        Assert.Contains("verdict", names);
        // R-14-3：tool 在透传集内；order 因归属硬闸 fail-closed 永不出现（API-06），拒绝必须可读。
        var tool = frames.First(f => f.Name == "tool");
        Assert.False(tool.Data.GetProperty("success").GetBoolean());
        Assert.Equal("proposal_not_found", tool.Data.GetProperty("errorCode").GetString());
        Assert.Contains("周期归属不匹配", tool.Data.GetProperty("error").GetString());
        Assert.DoesNotContain("event: order", sse);

        // 合成账户：includeMemory=false 时记忆完全不读库；写网关零调用；权益为默认 10000。
        Assert.Equal(0, host.AccountReader.DecisionMemoryCalls);
        Assert.Equal(0, host.AccountReader.AccountCalls);   // API-06：账户接口零请求
        Assert.Equal(0, host.AccountReader.PositionCalls);  // API-06：持仓接口零请求
        Assert.Equal(0, host.Gateway.PlaceCallCount);
        Assert.Equal(0, host.Gateway.CloseCallCount);
        Assert.Contains("权益 10000 USDT", host.Chat.PromptFor("trader"));

        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cycle = await db.TradingCycles.AsNoTracking().SingleAsync();
        Assert.Equal("test", cycle.Trigger);
        Assert.Equal("dry", cycle.Mode);
        Assert.Null(cycle.TraderId);
        Assert.Null(cycle.ExchangeAccountId);
        Assert.Equal(1, cycle.ProviderId);
        Assert.Equal("completed", cycle.Status);

        // 周期事件 traderId 一律 null（§3.3）。
        var cycleFrame = frames.First(f => f.Name == "cycle");
        Assert.Equal(JsonValueKind.Null, cycleFrame.Data.GetProperty("traderId").ValueKind);
    }

    [Fact]
    public async Task Test_run_rejects_bad_equity_string_real_account_and_legacy_shape_with_400()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        await AssertErrorAsync(await host.Client.PostAsJsonAsync("/api/strategies/test-run",
            new { strategyId = 1, providerId = 1, testEquity = "abc" }, ApiJson.Options),
            HttpStatusCode.BadRequest, "validation_failed");
        await AssertErrorAsync(await host.Client.PostAsJsonAsync("/api/strategies/test-run",
            new { strategyId = 1, providerId = 1, testEquity = "-5" }, ApiJson.Options),
            HttpStatusCode.BadRequest, "validation_failed");
        await AssertErrorAsync(await host.Client.PostAsJsonAsync("/api/strategies/test-run",
            new { strategyId = 1, providerId = 1, accountId = 1 }, ApiJson.Options),
            HttpStatusCode.BadRequest, "validation_failed");
        // 旧形状（config 内联 + 无 strategyId 必填语义）在新契约下被绑定/字段校验拒绝。
        await AssertErrorAsync(await host.Client.PostAsJsonAsync("/api/strategies/test-run",
            new { config = new StrategyConfig(), providerId = 1, strategyId = 1 }, ApiJson.Options),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(0, await host.CountCyclesAsync());
    }

    [Fact]
    public async Task Test_run_unknown_resources_return_wrapped_404()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host);

        await AssertErrorAsync(await host.Client.PostAsJsonAsync("/api/strategies/test-run",
            new { strategyId = 99, providerId = 1 }, ApiJson.Options), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await host.Client.PostAsJsonAsync("/api/strategies/test-run",
            new { strategyId = 1, providerId = 99 }, ApiJson.Options), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Legacy_preview_prompt_is_deleted()
    {
        await using var host = await TestApp.StartAsync();
        // 旧单数 /preview-prompt 已被 /preview-prompts 取代（R-14-8）：端点不再存在——
        // 必须既非 2xx、也不再返回旧 PromptPreviewDto（systemPrompt 裸对象）。
        var response = await host.Client.PostAsJsonAsync("/api/strategies/preview-prompt",
            new { config = new StrategyConfig() }, ApiJson.Options);
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("systemPrompt", body, StringComparison.Ordinal);
    }

    // —— 宿主与辅助 ——

    private sealed record SseFrame(string Name, JsonElement Data);

    private static List<SseFrame> ParseSse(string text)
    {
        var frames = new List<SseFrame>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            string? data = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line["event: ".Length..].Trim();
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line["data: ".Length..];
            }

            if (name is not null && data is not null)
            {
                frames.Add(new SseFrame(name, JsonDocument.Parse(data).RootElement.Clone()));
            }
        }

        return frames;
    }

    private sealed class TestApp : IAsyncDisposable
    {
        // 命名共享缓存内存库 + 每上下文独立连接（A 组报告 §9.4 的并发提示落地）：
        // 端点请求、后台调度循环与测试轮询会在同一时刻读写库——单连接共享模式下单连接并发
        // 直接抛 "database is locked"；共享缓存 + Default Timeout=30 让读写按 SQLite busy 语义排队。
        // keeper 连接贯穿宿主生命周期（内存库随最后一个连接销毁）。
        private readonly SqliteConnection _keeper;

        private TestApp(SqliteConnection keeper, WebApplication app, HttpClient client,
            HttpChatClient chat, HttpRunner runner, TestAccountReader accountReader, RecordingGateway gateway)
        {
            _keeper = keeper;
            App = app;
            Client = client;
            Chat = chat;
            Runner = runner;
            AccountReader = accountReader;
            Gateway = gateway;
            Hub = app.Services.GetRequiredService<SseEventHub>();
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public HttpChatClient Chat { get; }
        public HttpRunner Runner { get; }
        public TestAccountReader AccountReader { get; }
        public RecordingGateway Gateway { get; }
        public SseEventHub Hub { get; }

        public static async Task<TestApp> StartAsync(
            string analystBehavior = "brief", string executorBehavior = "clean", int closedTrades = 0)
        {
            var connectionString =
                $"Data Source=file:cyclehttptest-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=30";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var chat = new HttpChatClient(analystBehavior, executorBehavior);
            var accountReader = new TestAccountReader();
            var marketReader = new TestMarketReader();
            var gateway = new RecordingGateway();
            var reviewReader = new FakeReviewAccountReader(closedTrades);
            var hub = new SseEventHub();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));
            builder.Services.AddHttpClient();
            builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
            builder.Services.AddSingleton<ChatClientFactory>(chat);
            builder.Services.AddSingleton(hub);
            builder.Services.AddScoped<CycleRecorder>();
            builder.Services.AddScoped<ExchangeTradingService>();
            builder.Services.AddScoped<CoinRankingService>();
            builder.Services.AddScoped<MarketSnapshotService>();
            builder.Services.AddScoped<MarketSentimentService>();
            builder.Services.AddScoped<ExternalDataSourceService>();
            builder.Services.AddScoped<StrategyTestRunner>();
            builder.Services.AddScoped<DecisionMemoryService>();
            // 真实 AccountStreamService：stream 测试的交易员绑定缺失账户，绝不会触达 Acquire（零网络）。
            builder.Services.AddSingleton<AccountStreamService>(sp =>
                new AccountStreamService(sp.GetRequiredService<IServiceScopeFactory>(), hub));
            builder.Services.AddSingleton<TradingCycleRunner>(sp =>
                new HttpRunner(sp.GetRequiredService<IServiceScopeFactory>(), hub,
                    accountReader, marketReader, gateway));
            builder.Services.AddSingleton<ReviewRunner>(sp =>
                new HttpReviewRunner(sp.GetRequiredService<IServiceScopeFactory>(), hub, reviewReader));

            var app = builder.Build();
            using (var scope = app.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
            }

            app.UseApiErrorWrapper();
            app.MapProviders();
            app.MapExchanges();
            app.MapStrategies();
            app.MapTraders();
            app.MapCycles();
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            return new TestApp(keeper, app, client, chat,
                (HttpRunner)app.Services.GetRequiredService<TradingCycleRunner>(), accountReader, gateway);
        }

        public Task<int> CountCyclesAsync()
        {
            using var scope = App.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<AppDbContext>().TradingCycles.CountAsync();
        }

        public async Task<bool> HasRunningCycleAsync()
        {
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.TradingCycles.AsNoTracking().AnyAsync(c => c.Status == "running");
        }

        public async ValueTask DisposeAsync()
        {
            Runner.Stop(1);
            Runner.Stop(2);
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            _keeper.Dispose();
        }
    }

    /// <summary>周期宿主测试缝：账户/行情/网关全部走假实现（与 TradingCycleRunnerTests 同一口径）。</summary>
    private sealed class HttpRunner(IServiceScopeFactory scopeFactory, SseEventHub hub,
        IAccountReader accounts, IMarketDataReader market, IExecutionGateway gateway)
        : TradingCycleRunner(scopeFactory, hub)
    {
        protected override CycleDataSources CreateDataSources(IServiceProvider services, Trader trader,
            ExchangeAccount account, StrategyConfig config, string memoryFingerprint) =>
            new(accounts, market, gateway);
    }

    /// <summary>复盘宿主测试缝：只替换账户读取器（提供已平仓交易），其余走真实 ReviewRunner。</summary>
    private sealed class HttpReviewRunner(IServiceScopeFactory scopeFactory, SseEventHub hub,
        IAccountReader reader) : ReviewRunner(scopeFactory, hub)
    {
        protected override IAccountReader CreateReader(IServiceProvider services, Trader trader,
            ExchangeAccount account, StrategyConfig config, string memoryFingerprint) => reader;
    }

    private static async Task SeedAsync(TestApp host, bool providerVerified = true,
        bool danglingAccount = false)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!db.Providers.Any())
        {
            db.Providers.Add(new Provider
            {
                Id = 1, Name = "脚本提供商", Type = ProviderType.OpenAICompatible,
                BaseUrl = "https://api.test", Model = "m",
                ToolCallingSupported = providerVerified ? true : null,
                ToolCallingVerifiedAt = providerVerified ? DateTime.UtcNow : null,
            });
            db.ExchangeAccounts.Add(new ExchangeAccount
            {
                Id = 1, ExchangeType = ExchangeType.GateIo, AccountName = "脚本账户",
                ApiKey = "key-abcdefgh1234", ApiSecret = "secret-abcdefgh1234",
            });
            var config = new StrategyConfig { NewsEnabled = false };
            config.CoinSource.StaticCoins = ["BTC"];
            db.Strategies.Add(new Strategy
            {
                Id = 1, Name = "脚本策略",
                ConfigJson = JsonSerializer.Serialize(config, VerificationJson.Options),
            });
            db.Traders.Add(new Trader
            {
                Id = 1, Name = "脚本交易员", ProviderId = 1, ExchangeAccountId = 1, StrategyId = 1,
                ExecutionMode = "dry", ScanIntervalMinutes = 3,
            });
        }

        if (danglingAccount)
        {
            db.Traders.Add(new Trader
            {
                Id = 2, Name = "无账户交易员", ProviderId = 1, ExchangeAccountId = 999, StrategyId = 1,
                ExecutionMode = "dry", ScanIntervalMinutes = 3, InitialBalance = 5_000m,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedReviewStageAsync(TestApp host, string status, DateTime createdAt)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AgentStageRuns.Add(new AgentStageRun
        {
            Id = Ids.NewUlid(), CycleId = null, TraderId = 1, Role = "reviewer", Status = status,
            ProviderName = "脚本提供商", Model = "m", Instructions = "i", TranscriptJson = "[]",
            Error = status == "failed" ? "stage_failed：复盘阶段执行失败，本轮未产出经验" : null,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedForeignStageAsync(TestApp host)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AgentStageRuns.Add(new AgentStageRun
        {
            Id = Ids.NewUlid(), CycleId = null, TraderId = 1, Role = "trader", Status = "completed",
            ProviderName = "脚本提供商", Model = "m", Instructions = "i", TranscriptJson = "[]",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> CountReviewStagesAsync(TestApp host)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AgentStageRuns.CountAsync(r => r.Role == "reviewer" && r.TraderId == 1);
    }

    private static async Task SeedCyclesAsync(TestApp host, int count)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 1; i <= count; i++)
        {
            db.TradingCycles.Add(new TradingCycle
            {
                Id = $"01HTTPPAGECYCLE0000000000{i}", TraderId = 1, StrategyId = 1, ProviderId = 1,
                ExchangeAccountId = 1, Mode = "dry", Trigger = "schedule", Status = "completed",
                StartedAt = DateTime.UtcNow.AddMinutes(-i), CompletedAt = DateTime.UtcNow.AddMinutes(-i),
                StrategyConfigJson = "{}", MemoryFingerprint = "f",
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedLessonAsync(TestApp host)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        db.TraderLessons.Add(new TraderLesson
        {
            TraderId = 1, StageRunId = "01HTTPTESTSTAGE0000000009", PeriodStart = now.AddDays(-1), PeriodEnd = now,
            Summary = "复盘结论：守住止损就是纪律", StrengthsJson = "[]", MistakesJson = "[]",
            RulesJson = """["规则一","规则二"]""", EvidenceJson = "[]", CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>看板「最近订单」投影：模型里 Success 仍存在，但 DTO 不再输出（§2.2）。</summary>
    private static async Task SeedRecentOrderAsync(TestApp host, int traderId)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TraderOrders.Add(new TraderOrder
        {
            TraderId = traderId, At = DateTime.UtcNow, Action = "close", Symbol = "BTC/USDT",
            Quantity = 0.05m, Price = 101.5m, Status = "filled", Simulated = true, Success = true,
            Note = "看板订单投影",
        });
        await db.SaveChangesAsync();
    }

    private static async Task SetLastCycleStatusAsync(TestApp host, int traderId, string status)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var trader = await db.Traders.SingleAsync(t => t.Id == traderId);
        trader.LastCycleStatus = status;
        await db.SaveChangesAsync();
    }

    /// <summary>未知交易所类型：AcquireAsync 在拉 WS 时必然抛错，用来证明异常路径的订阅释放。</summary>
    private static async Task SeedUnsupportedExchangeTraderAsync(TestApp host)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ExchangeAccounts.Add(new ExchangeAccount
        {
            Id = 3, ExchangeType = (ExchangeType)999, AccountName = "异常交易所账户",
            ApiKey = "key-abcdefgh1234", ApiSecret = "secret-abcdefgh1234",
        });
        db.Traders.Add(new Trader
        {
            Id = 3, Name = "异常交易所交易员", ProviderId = 1, ExchangeAccountId = 3, StrategyId = 1,
            ExecutionMode = "dry", ScanIntervalMinutes = 3,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 总线订阅数（测试观察缝）：SseEventHub 没有公开计数，泄漏只能从内部字典观察；
    /// 生产侧不为测试新增 API。
    /// </summary>
    private static int SubscriberCount(SseEventHub hub, string key)
    {
        var field = typeof(SseEventHub).GetField("_subscribers",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var subscribers =
            (System.Collections.Concurrent.ConcurrentDictionary<string, List<System.Threading.Channels.Channel<SseEvent>>>)
            field.GetValue(hub)!;
        return subscribers.TryGetValue(key, out var list) ? list.Count : 0;
    }

    /// <summary>open 周期全套：4 阶段之一含截断标记、提案/裁决/订单/工具审计行。</summary>
    private static async Task SeedOpenCycleAsync(TestApp host, int traderId = 1)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        db.TradingCycles.Add(new TradingCycle
        {
            Id = "01HTTPTESTCYCLE000000001", TraderId = traderId, StrategyId = 1, ProviderId = 1, ExchangeAccountId = 1,
            Mode = "dry", Trigger = "schedule", Status = "completed", StartedAt = now, CompletedAt = now,
            StrategyConfigJson = "{}", MemoryFingerprint = "f", ElapsedMs = 1000,
        });
        db.AgentStageRuns.Add(new AgentStageRun
        {
            Id = "01HTTPTESTSTAGE0000000001", CycleId = "01HTTPTESTCYCLE000000001", TraderId = traderId,
            Role = "analyst", Status = "completed", ProviderName = "p", Model = "m",
            Instructions = "指令全文", TranscriptJson = """[{"role":"system","text":"…（消息记录已截断）"}]""",
            OutputJson = """{"marketContext":"BTC 震荡","candidates":["BTC/USDT"]}""",
            ElapsedMs = 400, InputTokens = 100, OutputTokens = 20, ToolCallCount = 1, Truncated = true,
            CreatedAt = now,
        });
        db.AgentStageRuns.Add(new AgentStageRun
        {
            Id = "01HTTPTESTSTAGE0000000002", CycleId = "01HTTPTESTCYCLE000000001", TraderId = traderId,
            Role = "trader", Status = "completed", ProviderName = "p", Model = "m",
            Instructions = "指令全文", TranscriptJson = "[]",
            OutputJson = """{"proposal":{"action":"open","symbol":"BTC/USDT","thesis":"趋势向上"}}""",
            ElapsedMs = 300, ToolCallCount = 0, CreatedAt = now,
        });
        db.ToolInvocations.Add(new ToolInvocation
        {
            CycleId = "01HTTPTESTCYCLE000000001", StageRunId = "01HTTPTESTSTAGE0000000001", ToolName = "get_klines",
            ArgumentsJson = "{}", ResultJson = "{}", Success = false, ErrorCode = "tool_budget_exceeded",
            ElapsedMs = 5, Truncated = false, CreatedAt = now,
        });
        db.TradeProposals.Add(new TradeProposal
        {
            Id = "01HTTPTESTPROPOSAL00000001", CycleId = "01HTTPTESTCYCLE000000001", TraderId = traderId,
            Action = "open", Symbol = "BTC/USDT", Side = "long", Confidence = 0.68m, EntryPrice = 100.25m,
            StopLossPrice = 95m, TakeProfitPrice = 110m, PositionPct = 5m, Leverage = 10m,
            Thesis = "4h 趋势向上", RawJson = "{}", CreatedAt = now,
        });
        db.RiskVerdicts.Add(new RiskVerdict
        {
            Id = "01HTTPTESTVERDICT000000001", CycleId = "01HTTPTESTCYCLE000000001",
            ProposalId = "01HTTPTESTPROPOSAL00000001", Decision = "approved",
            ReasonsJson = """["边界内"]""", MaxPositionPct = 3m, MaxLeverage = 5m,
            EntryZoneMin = 95m, EntryZoneMax = 105m, AllowMarket = false, ValidUntilUtc = now.AddMinutes(5),
            RawJson = "{}", CreatedAt = now,
        });
        db.TraderOrders.Add(new TraderOrder
        {
            TraderId = traderId, At = now, CycleId = "01HTTPTESTCYCLE000000001", ExchangeAccountId = 1,
            ProposalId = "01HTTPTESTPROPOSAL00000001", Action = "open", Side = "long", Symbol = "BTC/USDT",
            Quantity = 0.0180m, FilledQuantity = 0.0180m, Price = 100m, AveragePrice = 100m,
            Status = "filled", Simulated = true, Success = true, Note = "演练成交",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>hold 周期 + 游离裁决行：证明裁决只随 open 出现（API-08）。</summary>
    private static async Task SeedHoldCycleAsync(TestApp host)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        db.TradingCycles.Add(new TradingCycle
        {
            Id = "01HTTPTESTCYCLE000000002", TraderId = 1, StrategyId = 1, ProviderId = 1, ExchangeAccountId = 1,
            Mode = "dry", Trigger = "schedule", Status = "completed", StartedAt = now, CompletedAt = now,
            StrategyConfigJson = "{}", MemoryFingerprint = "f",
        });
        db.TradeProposals.Add(new TradeProposal
        {
            Id = "01HTTPTESTPROPOSAL00000002", CycleId = "01HTTPTESTCYCLE000000002", TraderId = 1,
            Action = "hold", Thesis = "条件未成熟", RawJson = "{}", CreatedAt = now,
        });
        db.RiskVerdicts.Add(new RiskVerdict
        {
            Id = "01HTTPTESTVERDICT000000002", CycleId = "01HTTPTESTCYCLE000000002",
            ProposalId = "01HTTPTESTPROPOSAL00000002", Decision = "rejected", ReasonsJson = "[]",
            RawJson = "{}", CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>试运行周期：traderId/exchangeAccountId 为 null，可独立取回（R-14-9）。</summary>
    private static async Task SeedTestCycleAsync(TestApp host)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        db.TradingCycles.Add(new TradingCycle
        {
            Id = "01HTTPTESTCYCLE000000003", TraderId = null, StrategyId = 1, ProviderId = 1,
            ExchangeAccountId = null, Mode = "dry", Trigger = "test", Status = "completed",
            StartedAt = now, CompletedAt = now, StrategyConfigJson = "{}", MemoryFingerprint = "f",
        });
        await db.SaveChangesAsync();
    }

    private static List<SseEvent> Drain(System.Threading.Channels.ChannelReader<SseEvent> reader)
    {
        var list = new List<SseEvent>();
        while (reader.TryRead(out var evt)) list.Add(evt);
        return list;
    }

    private static JsonElement Serialize(SseEvent evt) =>
        JsonSerializer.SerializeToElement(evt.Payload, ApiJson.Options);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail($"等待条件在 {timeoutMs}ms 内未成立");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task AssertWrappedOkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string? messagePart = null)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        var message = body.GetProperty("error").GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        if (messagePart is not null) Assert.Contains(messagePart, message);
    }

    // —— 脚本化边界实现（与 TradingCycleRunnerTests 同口径的精简版）——

    /// <summary>按角色工具集分派的脚本模型；executorBehavior=order 时执行员发起一次被硬闸拒绝的写调用。</summary>
    private sealed class HttpChatClient(string analystBehavior, string executorBehavior)
        : ChatClientFactory, IChatClient
    {
        private readonly Dictionary<string, string> _prompts = new(StringComparer.Ordinal);
        private int _executorRound;

        public int Calls { get; private set; }

        public string PromptFor(string role) => _prompts.GetValueOrDefault(role, "");

        public override IChatClient Create(Provider provider) => this;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var role = DetectRole(options?.Tools);
            if (role is not null && messages.LastOrDefault() is { } user) _prompts[role] = user.Text;

            if (role == "analyst" && analystBehavior == "hang")
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return Chat(BriefJson);
            }

            if (role == "analyst" && Calls == 1 && analystBehavior == "brief")
            {
                // 首轮先调一次读工具，保证 tool 审计行与阶段计数非空。
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "get_klines",
                        new Dictionary<string, object?> { ["symbol"] = "BTC/USDT", ["timeframe"] = "5m", ["bars"] = 30 })]));
            }

            if (role == "executor" && executorBehavior == "order" && ++_executorRound == 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-order", "place_order", new Dictionary<string, object?>
                    {
                        ["proposalId"] = "01HTTPTESTPROPOSAL00000001",
                        ["symbol"] = "BTC/USDT",
                        ["side"] = "long",
                        ["orderType"] = "market",
                        ["quantity"] = 0.01m,
                        ["price"] = null,
                        ["leverage"] = 5m,
                        ["stopLossPrice"] = 95m,
                        ["takeProfitPrice"] = 110m,
                        ["reason"] = "试运行写闸回归",
                    })]));
            }

            var json = role switch
            {
                "analyst" => BriefJson,
                "trader" => ProposalJson,
                "risk" => VerdictJson,
                "executor" => ReportJson,
                "reviewer" => ReviewJson,
                _ => BriefJson,
            };
            return Chat(json);
        }

        private static ChatResponse Chat(string json) =>
            new(new ChatMessage(ChatRole.Assistant, json));

        private static string? DetectRole(IList<AITool>? tools)
        {
            if (tools is null) return null;
            var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            if (names.Contains("get_cycles")) return "reviewer";
            if (names.Contains("get_coin_candidates")) return "analyst";
            if (names.Contains("get_decision_memory")) return "trader";
            if (names.Contains("get_risk_policy")) return "risk";
            if (names.Contains("place_order")) return "executor";
            return null;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private const string BriefJson =
            """{"marketContext":"BTC 震荡上行","candidates":["BTC/USDT"],"symbols":[],"dataWarnings":[]}""";

        private const string ProposalJson =
            """{"action":"open","symbol":"BTC/USDT","side":"long","confidence":0.8,"entryPrice":100,"stopLossPrice":95,"takeProfitPrice":110,"positionPct":1,"leverage":5,"thesis":"趋势向上，回踩确认","invalidation":"跌破 90 失效","reviewChecklist":[]}""";

        private static string VerdictJson =>
            $$"""{"decision":"approved","reasons":["边界内"],"maxPositionPct":1,"maxLeverage":5,"entryZoneMin":95,"entryZoneMax":105,"allowMarket":true,"validUntilUtc":"{{DateTime.UtcNow.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture)}}"}""";

        private const string ReportJson =
            """{"summary":"本轮未提交写请求","attempts":[],"orders":[],"errors":[]}""";

        private const string ReviewJson =
            """{"summary":"复盘结论：守住止损就是纪律","strengths":[],"mistakes":[],"rules":["不开没有止损的仓位"],"evidence":[]}""";
    }

    /// <summary>账户读取假实现：恒有 BTC 多仓（手动平仓演练可成交），记忆读库计数（证明 includeMemory 门控）。</summary>
    private sealed class TestAccountReader : IAccountReader
    {
        public int DecisionMemoryCalls { get; private set; }
        public int AccountCalls { get; private set; }
        public int PositionCalls { get; private set; }

        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)
        {
            AccountCalls++;
            return Task.FromResult(new AccountSnapshot(true, 10_000m, 9_000m, null));
        }

        public Task<PositionReadResult> GetPositionsAsync(CancellationToken ct)
        {
            PositionCalls++;
            return Task.FromResult(new PositionReadResult(true,
                [new SymbolPosition("BTC/USDT", SharedPositionSide.Long, 0.1m, 100m, 1m, 80m, 5m)], null));
        }

        public Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderOrder>>([]);

        public Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<PositionHistory>>([]);

        public Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct)
        {
            DecisionMemoryCalls++;
            return Task.FromResult<IReadOnlyList<TradeProposal>>([]);
        }

        public Task<RiskControlConfig> GetRiskPolicyAsync(CancellationToken ct) =>
            Task.FromResult(new RiskControlConfig());

        public Task<TradingDiscipline> GetDisciplineAsync(CancellationToken ct) =>
            Task.FromResult(new TradingDiscipline(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1),
                0, false, true));

        public Task<IReadOnlyList<TradingCycle>> GetCyclesAsync(DateTime sinceUtc, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TradingCycle>>([]);

        public Task<IReadOnlyList<TraderLesson>> GetLessonsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderLesson>>([]);
    }

    /// <summary>复盘取数假实现：只提供固定条数的已平仓交易（无仓、无周期 → 其余复盘判据不变）。</summary>
    private sealed class FakeReviewAccountReader(int closedTrades) : IAccountReader
    {
        private readonly List<PositionHistory> _trades = Enumerable.Range(0, closedTrades).Select(_ =>
            new PositionHistory
            {
                ExchangeAccountId = 1, DedupeKey = Ids.NewUlid(), Source = "test", Symbol = "BTC/USDT",
                Side = "long", Quantity = 0.1m, EntryPrice = 100m, ExitPrice = 110m, RealizedPnl = 1m,
                Fee = 0.1m, CloseTime = DateTime.UtcNow.AddMinutes(-30), SyncedAt = DateTime.UtcNow,
            }).ToList();

        public Task<AccountSnapshot> GetAccountAsync(CancellationToken ct) =>
            Task.FromResult(new AccountSnapshot(true, 10_000m, 9_000m, null));

        public Task<PositionReadResult> GetPositionsAsync(CancellationToken ct) =>
            Task.FromResult(new PositionReadResult(true, [], null));

        public Task<IReadOnlyList<TraderOrder>> GetRecentOrdersAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderOrder>>([]);

        public Task<IReadOnlyList<PositionHistory>> GetClosedTradesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<PositionHistory>>(_trades);

        public Task<IReadOnlyList<TradeProposal>> GetDecisionMemoryAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TradeProposal>>([]);

        public Task<RiskControlConfig> GetRiskPolicyAsync(CancellationToken ct) =>
            Task.FromResult(new RiskControlConfig());

        public Task<TradingDiscipline> GetDisciplineAsync(CancellationToken ct) =>
            Task.FromResult(new TradingDiscipline(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1),
                0, false, true));

        public Task<IReadOnlyList<TradingCycle>> GetCyclesAsync(DateTime sinceUtc, int limit,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<TradingCycle>>([]);

        public Task<IReadOnlyList<TraderLesson>> GetLessonsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TraderLesson>>([]);
    }

    private sealed class TestMarketReader : IMarketDataReader
    {
        public Task<MarketSnapshot> GetSnapshotAsync(string exchange, SharedSymbol symbol, string timeframe,
            int bars, int warmupBars, CancellationToken ct)
        {
            var lastClose = DateTime.UtcNow.AddMinutes(-5);
            var span = TimeSpan.FromMinutes(5);
            var klines = Enumerable.Range(0, 30)
                .Select(i => new MarketKline(lastClose - span * (30 - i), 99m, 101m, 98m, 100m, 10m))
                .ToList();
            return Task.FromResult(new MarketSnapshot("live", $"{symbol.BaseAsset}/{symbol.QuoteAsset}",
                100m, klines, AsOfUtc: DateTime.UtcNow));
        }

        public Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) =>
            Task.FromResult<MarketSnapshot?>(null);

        public Task<IReadOnlyDictionary<string, SymbolSentiment>> GetSentimentAsync(string exchange,
            IReadOnlyList<SharedSymbol> symbols, bool includeOi, bool includeFunding, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, SymbolSentiment>>(new Dictionary<string, SymbolSentiment>());

        public Task<IReadOnlyList<ExternalDataResult>> GetNewsAsync(IEnumerable<ExternalDataSource> sources,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalDataResult>>([]);

        public Task<CoinSourceResolution?> GetCandidatesAsync(StrategyConfig config, CancellationToken ct) =>
            Task.FromResult<CoinSourceResolution?>(null);
    }

    /// <summary>写网关只计数：任何调用都会让「零交易」断言失败（演练拒绝路径不触网关）。</summary>
    private sealed class RecordingGateway : IExecutionGateway
    {
        public int PlaceCallCount { get; private set; }
        public int CloseCallCount { get; private set; }

        public Task<decimal?> GetPriceAsync(SharedSymbol symbol, CancellationToken ct) =>
            Task.FromResult<decimal?>(100m);

        public Task<SharedFuturesSymbol?> GetFuturesSpecAsync(SharedSymbol symbol, CancellationToken ct) =>
            Task.FromResult<SharedFuturesSymbol?>(null);

        public Task<(bool Ok, string? Error)> SetLeverageAsync(SharedSymbol symbol, decimal leverage,
            CancellationToken ct) => Task.FromResult((true, (string?)null));

        public Task<SharedPositionMode?> GetPositionModeAsync(CancellationToken ct) =>
            Task.FromResult<SharedPositionMode?>(SharedPositionMode.OneWay);

        public Task<PlaceResult> PlaceAsync(SharedSymbol symbol, bool isLong, decimal quantity,
            decimal stopLossPrice, decimal? takeProfitPrice, decimal leverage, string orderType, decimal? price,
            string? clientOrderId, CancellationToken ct)
        {
            PlaceCallCount++;
            return Task.FromResult(PlaceResult.NotCreated("测试网关不下单"));
        }

        public Task<CloseResult> CloseAsync(SharedSymbol symbol, decimal? quantity, bool isLong,
            string orderType, decimal? price, string? clientOrderId, CancellationToken ct)
        {
            CloseCallCount++;
            return Task.FromResult(CloseResult.Rejected("测试网关不平仓"));
        }

        public Task<CancelResult> CancelAsync(SharedSymbol symbol, string orderId, CancellationToken ct) =>
            Task.FromResult(CancelResult.Rejected("测试网关不撤单"));

        public TradingAudit Audit() => new(false, false, false, false, false, false, ["测试网关不具备交易能力"]);
    }
}
