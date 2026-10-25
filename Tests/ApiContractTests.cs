using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Endpoints;
using LuckyCat.Models;
using LuckyCat.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace LuckyCat.Tests;

/// <summary>
/// Task 14 A 组 HTTP 契约测试：统一包裹、/api/health、请求绑定（ApiJson 约定）、
/// provider 工具能力回写与连接字段编辑重置（API-01/02/07/09）。
/// 测试宿主按 RoundHttpTests 模式手工装配：只映射端点、不注册任何 hosted service
/// （CycleStartupRecovery / ReviewScheduler / PositionHistorySyncService / TraderRunnerService
/// 启动即做真实恢复/复盘/交易所调用，会让 HTTP 测试失真）；行情/账户/执行边界接口注册为
/// “解析即抛”守卫——A 组端点必须一个都不触达；模型调用只走脚本化假 IChatClient，零真实网络。
/// </summary>
public class ApiContractTests
{
    // —— /api/health（LC-STD-03 §3.6）——

    [Fact]
    public async Task Health_returns_wrapped_status()
    {
        await using var host = await TestApp.StartAsync();
        var response = await host.Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.False(body.TryGetProperty("error", out _));
        var data = body.GetProperty("data");
        Assert.Equal("ok", data.GetProperty("status").GetString());
        Assert.True(data.GetProperty("database").GetBoolean());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("version").GetString()));
        Assert.True(data.GetProperty("uptimeSeconds").GetInt64() >= 0);
        var timestamp = DateTimeOffset.Parse(data.GetProperty("timestamp").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
    }

    // —— 统一包裹与错误码（API-01/02）——

    [Fact]
    public async Task Providers_list_and_create_are_wrapped_with_camel_case_enums()
    {
        await using var host = await TestApp.StartAsync();
        var listBody = await ReadJsonAsync(await host.Client.GetAsync("/api/providers"));
        Assert.True(listBody.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Array, listBody.GetProperty("data").ValueKind);
        Assert.Empty(listBody.GetProperty("data").EnumerateArray());

        var create = await host.Client.PostAsJsonAsync("/api/providers", new
        {
            name = "契约测试",
            type = "openAICompatible",
            baseUrl = "https://api.test",
            apiKey = "<token>",
            model = "gpt-test",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await ReadJsonAsync(create);
        Assert.True(created.GetProperty("success").GetBoolean());
        var provider = created.GetProperty("data");
        Assert.Equal("openAICompatible", provider.GetProperty("type").GetString());
        // 密钥不回显明文：只允许掩码（LC-STD-02）
        Assert.DoesNotContain("<token>", provider.GetProperty("apiKeyMasked").GetString());
    }

    [Fact]
    public async Task Provider_create_invalid_body_returns_400_validation_failed()
    {
        await using var host = await TestApp.StartAsync();
        var response = await host.Client.PostAsJsonAsync("/api/providers", new
        {
            name = "坏配置",
            type = "openAICompatible",
            baseUrl = "不是URL",
            apiKey = "<token>",
            model = "m",
        }, ApiJson.Options);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Request_with_unknown_field_returns_400_validation_failed()
    {
        // 证明请求绑定真的启用了 ApiJson 约定（UnmappedMemberHandling.Disallow，LC-STD-03 §1.1）
        await using var host = await TestApp.StartAsync();
        var response = await host.Client.PostAsync("/api/providers", new StringContent(
            """
            {"name":"多字段","type":"openAICompatible","baseUrl":"https://api.test","apiKey":"<token>","model":"m","unexpectedField":1}
            """,
            Encoding.UTF8, "application/json"));
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Missing_resources_return_wrapped_404_not_found()
    {
        await using var host = await TestApp.StartAsync();
        await AssertErrorAsync(await host.Client.PutAsJsonAsync("/api/providers/999",
            new { name = "n", type = "openAICompatible", baseUrl = "https://api.test", apiKey = "<token>", model = "m" },
            ApiJson.Options), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await host.Client.DeleteAsync("/api/strategies/999"),
            HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await host.Client.DeleteAsync("/api/exchanges/999"),
            HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Delete_referenced_resources_return_409_resource_in_use()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, db =>
        {
            db.Providers.Add(new Provider { Id = 1, Name = "p", Type = ProviderType.OpenAICompatible, BaseUrl = "https://api.test", Model = "m" });
            db.ExchangeAccounts.Add(new ExchangeAccount { Id = 1, ExchangeType = ExchangeType.Binance, AccountName = "a", ApiKey = "key-abcdefgh1234", ApiSecret = "secret-abcdefgh1234" });
            db.Strategies.Add(new Strategy { Id = 1, Name = "s", ConfigJson = "{}" });
            db.Traders.Add(new Trader { Id = 1, Name = "t", ProviderId = 1, ExchangeAccountId = 1, StrategyId = 1 });
        });

        await AssertErrorAsync(await host.Client.DeleteAsync("/api/providers/1"),
            HttpStatusCode.Conflict, "resource_in_use");
        await AssertErrorAsync(await host.Client.DeleteAsync("/api/exchanges/1"),
            HttpStatusCode.Conflict, "resource_in_use");
        await AssertErrorAsync(await host.Client.DeleteAsync("/api/strategies/1"),
            HttpStatusCode.Conflict, "resource_in_use");
    }

    [Fact]
    public async Task Delete_strategy_referenced_by_cycle_returns_409_and_unreferenced_provider_returns_204()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, db =>
        {
            db.Providers.Add(new Provider { Id = 1, Name = "p", Type = ProviderType.OpenAICompatible, BaseUrl = "https://api.test", Model = "m" });
            db.Strategies.Add(new Strategy { Id = 1, Name = "s", ConfigJson = "{}" });
            db.TradingCycles.Add(new TradingCycle
            {
                Id = "01TESTCYCLEID000000000001", StrategyId = 1, Mode = "dry", Trigger = "test",
                Status = "completed", StrategyConfigJson = "{}", MemoryFingerprint = "fp",
            });
        });

        // 周期引用同样阻止删除（LC-STD-03 §3.3：被交易员/周期引用返回 409）
        await AssertErrorAsync(await host.Client.DeleteAsync("/api/strategies/1"),
            HttpStatusCode.Conflict, "resource_in_use");

        var response = await host.Client.DeleteAsync("/api/providers/1");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Unexpected_exception_returns_wrapped_500_internal_error_without_leak()
    {
        await using var host = await TestApp.StartAsync(probeThrow: true);
        var response = await host.Client.GetAsync("/api/__probe-throw");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("internal_error", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("服务内部错误，请稍后重试", body.GetProperty("error").GetProperty("message").GetString());
        // 堆栈、异常类型名、模拟的密钥与上游地址一律不得出现在响应里（LC-STD-02）
        var raw = body.ToString();
        Assert.DoesNotContain("模拟未预期异常", raw);
        Assert.DoesNotContain("api.example", raw);
        Assert.DoesNotContain("<token>", raw);
    }

    // —— 交易所：包裹与密钥掩码（LC-STD-02/§3.2）——

    [Fact]
    public async Task Exchange_create_is_wrapped_and_never_echoes_secrets()
    {
        await using var host = await TestApp.StartAsync();
        var response = await host.Client.PostAsJsonAsync("/api/exchanges", new
        {
            exchangeType = "binance",
            accountName = "主账户",
            apiKey = "abcdefgh1234",
            apiSecret = "supersecretvalue",
            passphrase = (string?)null,
            userId = (string?)null,
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        var text = body.ToString();
        Assert.DoesNotContain("supersecretvalue", text);
        Assert.DoesNotContain("abcdefgh1234", text);
        Assert.Equal("主账户", body.GetProperty("data").GetProperty("accountName").GetString());
    }

    // —— 策略 CRUD 包裹 ——

    [Fact]
    public async Task Strategy_create_activate_duplicate_and_default_config_are_wrapped()
    {
        await using var host = await TestApp.StartAsync();
        var config = new StrategyConfig { NewsEnabled = false };
        config.CoinSource.StaticCoins = ["BTC"];

        var create = await host.Client.PostAsJsonAsync("/api/strategies",
            new StrategyUpsertRequest("契约策略", "描述", config), ApiJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await ReadJsonAsync(create);
        Assert.True(created.GetProperty("success").GetBoolean());
        var strategyId = created.GetProperty("data").GetProperty("id").GetInt32();

        var activate = await host.Client.PostAsync($"/api/strategies/{strategyId}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        var activated = await ReadJsonAsync(activate);
        Assert.True(activated.GetProperty("data").GetProperty("isActive").GetBoolean());

        var duplicate = await host.Client.PostAsync($"/api/strategies/{strategyId}/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);

        var defaults = await ReadJsonAsync(await host.Client.GetAsync("/api/strategies/default-config"));
        Assert.True(defaults.GetProperty("success").GetBoolean());
        Assert.True(defaults.GetProperty("data").GetProperty("config").GetProperty("riskControl").ValueKind is JsonValueKind.Object);
    }

    // —— validate（LC-STD-03 §3.3）——

    [Fact]
    public async Task Validate_accepts_valid_config_and_names_invalid_field()
    {
        await using var host = await TestApp.StartAsync();
        var valid = await host.Client.PostAsJsonAsync("/api/strategies/validate",
            new PromptPreviewRequest(new StrategyConfig()), ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var validBody = await ReadJsonAsync(valid);
        Assert.True(validBody.GetProperty("data").GetProperty("valid").GetBoolean());
        Assert.Equal(0, validBody.GetProperty("data").GetProperty("errors").GetArrayLength());

        var bad = new StrategyConfig();
        bad.RiskControl.MaxMarginUsage = 101;
        var invalid = await host.Client.PostAsJsonAsync("/api/strategies/validate",
            new PromptPreviewRequest(bad), ApiJson.Options);
        await AssertErrorAsync(invalid, HttpStatusCode.BadRequest, "validation_failed", "保证金使用率");
    }

    // —— preview-prompts（§3.3：五角色提示词与估算 token，不调用模型）——

    [Fact]
    public async Task Preview_prompts_returns_five_roles_without_model_call_or_db_writes()
    {
        await using var host = await TestApp.StartAsync();
        var config = new StrategyConfig { NewsEnabled = false };
        config.CoinSource.StaticCoins = ["BTC"];
        var fingerprintBefore = PromptLibrary.MemoryFingerprint(config);

        var response = await host.Client.PostAsJsonAsync("/api/strategies/preview-prompts",
            new PromptPreviewRequest(config), ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var roles = body.GetProperty("data").GetProperty("roles").EnumerateArray().ToList();
        Assert.Equal(5, roles.Count);
        Assert.Equal("analyst,trader,risk,executor,reviewer",
            string.Join(",", roles.Select(r => r.GetProperty("role").GetString())));

        foreach (var (role, element) in roles.Select((r, i) => (role: (AgentRole)i, element: r)))
        {
            var prompt = element.GetProperty("prompt").GetString()!;
            // 预览必须与 PromptLibrary.Build 同一实现、同一产物（LC-STD-04 §7：预览不改模板）
            Assert.Equal(PromptLibrary.Build(role, config), prompt);
            Assert.Contains("只能通过提供的工具取数与下单", prompt);
            Assert.True(element.GetProperty("estimatedTokens").GetInt32() > 0);
        }

        Assert.Equal(0, host.Chat.Calls); // 不调用模型
        Assert.Equal(fingerprintBefore, PromptLibrary.MemoryFingerprint(config)); // 指纹不受影响
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.TradingCycles.CountAsync()); // 预览无落库副作用
            Assert.Equal(0, await db.AgentStageRuns.CountAsync());
        }
    }

    [Fact]
    public async Task Preview_prompts_rejects_invalid_config_with_400()
    {
        await using var host = await TestApp.StartAsync();
        var bad = new StrategyConfig();
        bad.RiskControl.MaxMarginUsage = 101;
        var response = await host.Client.PostAsJsonAsync("/api/strategies/preview-prompts",
            new PromptPreviewRequest(bad), ApiJson.Options);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_failed");
    }

    // —— Provider 工具能力：验证回写与连接字段重置（API-09）——

    [Fact]
    public async Task Verify_tools_pass_writes_back_capability()
    {
        await using var host = await TestApp.StartAsync(toolsPass: true);
        await SeedAsync(host, db => db.Providers.Add(new Provider
        {
            Id = 1, Name = "p", Type = ProviderType.OpenAICompatible, BaseUrl = "https://api.test", Model = "m",
        }));

        var response = await host.Client.PostAsync("/api/providers/1/verify", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: ", sse);

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/providers"));
        var provider = body.GetProperty("data")[0];
        Assert.True(provider.GetProperty("toolCallingSupported").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, provider.GetProperty("toolCallingVerifiedAt").ValueKind);
    }

    [Fact]
    public async Task Verify_tools_fail_writes_back_false()
    {
        await using var host = await TestApp.StartAsync(toolsPass: false);
        await SeedAsync(host, db => db.Providers.Add(new Provider
        {
            Id = 1, Name = "p", Type = ProviderType.OpenAICompatible, BaseUrl = "https://api.test", Model = "m",
            ToolCallingSupported = true, ToolCallingVerifiedAt = DateTime.UtcNow.AddMinutes(-5),
        }));

        await (await host.Client.PostAsync("/api/providers/1/verify", null)).Content.ReadAsStringAsync();

        var body = await ReadJsonAsync(await host.Client.GetAsync("/api/providers"));
        Assert.False(body.GetProperty("data")[0].GetProperty("toolCallingSupported").GetBoolean());
    }

    [Fact]
    public async Task Editing_connection_fields_resets_capability_but_other_edits_keep_it()
    {
        await using var host = await TestApp.StartAsync();
        await SeedAsync(host, db => db.Providers.Add(new Provider
        {
            Id = 1, Name = "p", Type = ProviderType.OpenAICompatible, BaseUrl = "https://api.test",
            ApiKey = "old-key", Model = "m", ToolCallingSupported = true,
            ToolCallingVerifiedAt = new DateTime(2026, 10, 25, 5, 0, 0, DateTimeKind.Utc),
        }));

        // 只改名称与思考模式：连接身份未变，能力字段必须保留
        var rename = await host.Client.PutAsJsonAsync("/api/providers/1", new
        {
            name = "p2", type = "openAICompatible", baseUrl = "https://api.test",
            apiKey = (string?)null, model = "m", thinkingMode = "on",
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var renamed = (await ReadJsonAsync(rename)).GetProperty("data");
        Assert.True(renamed.GetProperty("toolCallingSupported").GetBoolean());

        // 改模型 = 连接身份变化：两个能力字段重置（LC-STD-03 §2.2）
        var modelChange = await host.Client.PutAsJsonAsync("/api/providers/1", new
        {
            name = "p2", type = "openAICompatible", baseUrl = "https://api.test",
            apiKey = (string?)null, model = "m2",
        }, ApiJson.Options);
        var changed = (await ReadJsonAsync(modelChange)).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("toolCallingSupported").ValueKind);
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("toolCallingVerifiedAt").ValueKind);

        // 换 Key 同样重置
        await SeedAsync(host, db =>
        {
            var provider = db.Providers.Find(1)!;
            provider.ToolCallingSupported = true;
            provider.ToolCallingVerifiedAt = new DateTime(2026, 10, 25, 5, 0, 0, DateTimeKind.Utc);
            db.SaveChanges();
        });
        var keyChange = await host.Client.PutAsJsonAsync("/api/providers/1", new
        {
            name = "p2", type = "openAICompatible", baseUrl = "https://api.test",
            apiKey = "new-key", model = "m2",
        }, ApiJson.Options);
        Assert.Equal(JsonValueKind.Null,
            ((await ReadJsonAsync(keyChange)).GetProperty("data")).GetProperty("toolCallingSupported").ValueKind);
    }

    // —— 切流证明（R-14-5）：旧 TraderRunnerService 的 hosted 注册必须整体摘除，调度只剩新链路 ——

    [Fact]
    public void Program_has_no_legacy_hosted_scheduler_and_maps_cycles_endpoints()
    {
        // 生产 Program 是顶级语句、无法以 WebApplicationFactory 直接起宿主；
        // 用源码扫描钉死「恰好一个调度链」的注册形状——这正是 Task 11 R-11-6 要防的双调度器缺陷面。
        var program = ReadProductionFile("Program.cs");
        Assert.DoesNotContain("AddSingleton<TraderRunnerService>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService(sp => sp.GetRequiredService<TraderRunnerService>())",
            program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService(sp => sp.GetRequiredService<TraderRunnerService>",
            program, StringComparison.Ordinal);
        // 新链路：恰好一个恢复宿主 + 单例周期引擎 + 周期端点已接线。
        Assert.Single(CountOccurrences(program, "AddHostedService<CycleStartupRecovery>"));
        Assert.Contains("AddSingleton<TradingCycleRunner>", program, StringComparison.Ordinal);
        Assert.Contains("MapCycles", program, StringComparison.Ordinal);
        // 旧类仍要能编译（Task 18 才删除）。
        Assert.True(File.Exists(Path.Combine(FindRepoRoot(), "Services", "TraderRunnerService.cs")));
    }

    private static IEnumerable<string> CountOccurrences(string text, string needle)
    {
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            yield return needle;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuckyCat.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("未找到 LuckyCat.csproj 所在目录");
    }

    private static string ReadProductionFile(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    // —— 宿主与辅助 ——

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestApp(SqliteConnection connection, WebApplication app, HttpClient client, ScriptedChatClient chat)
        {
            _connection = connection;
            App = app;
            Client = client;
            Chat = chat;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public ScriptedChatClient Chat { get; }

        public static async Task<TestApp> StartAsync(bool toolsPass = true, bool probeThrow = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var chat = new ScriptedChatClient(toolsPass);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
            builder.Services.AddHttpClient();
            // 与生产 Program 同一配置入口：证明的绑定行为即运行时行为（camelCase/camelCase 枚举/Disallow）
            builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
            // 模型调用只走脚本化假客户端：零真实网络、零真实上游
            builder.Services.AddSingleton<ChatClientFactory>(new ScriptedChatFactory(chat));
            builder.Services.AddScoped<VerificationRunner>();
            // 端点参数推断要求映射过的端点依赖可解析；测试从不调用真实行情/交易所路径
            builder.Services.AddScoped<CoinRankingService>();
            builder.Services.AddScoped<MarketSnapshotService>();
            builder.Services.AddScoped<MarketSentimentService>();
            builder.Services.AddScoped<ExternalDataSourceService>();
            builder.Services.AddScoped<StrategyTestRunner>();
            builder.Services.AddScoped<DecisionMemoryService>();
            builder.Services.AddScoped<ExchangeTradingService>();
            builder.Services.AddScoped<ExchangeVerificationRunner>();
            builder.Services.AddSingleton<SseEventHub>();
            // B 组接线后策略端点新增 /test-run（TradingCycleRunner）；A 组用例绝不触发它，
            // 这里只为满足端点参数推断的「可解析」要求——周期/复盘宿主同样只注册、不调用（§4）。
            builder.Services.AddSingleton<TradingCycleRunner>();
            builder.Services.AddSingleton<ReviewRunner>();
            // A 组端点绝不触达行情/账户/执行边界：注册即抛守卫，误触达会让测试立刻失败
            builder.Services.AddSingleton<IMarketDataReader>(_ => throw new NotSupportedException("A 组端点不得触达行情接口"));
            builder.Services.AddSingleton<IAccountReader>(_ => throw new NotSupportedException("A 组端点不得触达账户接口"));
            builder.Services.AddSingleton<IExecutionGateway>(_ => throw new NotSupportedException("A 组端点不得触达执行网关"));

            var app = builder.Build();
            using (var scope = app.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
            }
            app.UseApiErrorWrapper();
            app.MapProviders();
            app.MapExchanges();
            app.MapStrategies();
            app.MapHealth();
            if (probeThrow)
            {
                app.MapGet("/api/__probe-throw", (HttpContext _) =>
                    throw new InvalidOperationException("模拟未预期异常 https://api.example.com?key=<token>"));
            }
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            return new TestApp(connection, app, client, chat);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            _connection.Dispose();
        }
    }

    private static async Task SeedAsync(TestApp host, Action<AppDbContext> seed)
    {
        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
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

    private sealed class ScriptedChatFactory(IChatClient client) : ChatClientFactory
    {
        public override IChatClient Create(Provider provider) => client;
    }

    /// <summary>VerificationRunner 四项检查的脚本化假客户端：basic/流式/工具/JSON，工具结果可注入失败。</summary>
    private sealed class ScriptedChatClient(bool toolsPass) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (options?.Tools is { Count: > 0 })
            {
                if (!toolsPass) throw new InvalidOperationException("模拟模型服务失败");
                var call = new FunctionCallContent("call-1", "get_weather",
                    new Dictionary<string, object?> { ["city"] = "北京" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var text = options?.ResponseFormat is not null ? """{"ok":true,"value":42}""" : "OK";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "1 2 3 4 5");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
