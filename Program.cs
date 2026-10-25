using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Endpoints;
using LuckyCat.Services;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5210");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=luckycat.db"));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // 属性命名与 ApiJson.Options 对齐（camelCase、大小写不敏感、忽略未映射字段以兼容旧客户端）。
    options.SerializerOptions.PropertyNamingPolicy = ApiJson.Options.PropertyNamingPolicy;
    options.SerializerOptions.PropertyNameCaseInsensitive = ApiJson.Options.PropertyNameCaseInsensitive;
    // 旧端点仍依赖 PascalCase 枚举（ProviderType/ExchangeType 由现前端按值匹配），
    // 保留旧 JsonStringEnumConverter 直到 P7 端点重写；新代码使用 ApiJson.Options / AgentJson.Options 的 camelCase 枚举。
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddScoped<ChatClientFactory>();
builder.Services.AddScoped<VerificationRunner>();
builder.Services.AddScoped<ExchangeVerificationRunner>();
builder.Services.AddScoped<MarketSnapshotService>();
builder.Services.AddScoped<CoinRankingService>();
builder.Services.AddScoped<MarketSentimentService>();
builder.Services.AddScoped<ExternalDataSourceService>();
builder.Services.AddScoped<StrategyTestRunner>();
builder.Services.AddScoped<DecisionMemoryService>();
builder.Services.AddScoped<ExchangeTradingService>();
builder.Services.AddSingleton<TraderRunnerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TraderRunnerService>());
builder.Services.AddSingleton<PositionHistorySyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PositionHistorySyncService>());
builder.Services.AddSingleton<AccountStreamService>();
builder.Services.AddSingleton<SseEventHub>();
// 统一弹性管道（情绪/新闻等所有经 HttpClientFactory 的请求）：突发超时最多重试 3 次、线性退避 1/2/3 秒
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Linear;
        options.Retry.UseJitter = false;
        options.Retry.OnRetry = args =>
        {
            var reason = args.Outcome.Exception?.GetType().Name
                ?? args.Outcome.Result?.StatusCode.ToString()
                ?? "失败";
            Console.WriteLine($"[Http] {args.Outcome.Result?.RequestMessage?.RequestUri?.Host ?? "请求"} 第 {args.AttemptNumber + 1} 次重试（{reason}）");
            return ValueTask.CompletedTask;
        };
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
    }));

// OpenTelemetry：基础设施埋点（ASP.NET Core / HttpClient / 运行时）+ 业务埋点（Telemetry 源与指标）
// 导出端按环境变量启用（无收集端时零噪声）：OTEL_EXPORTER_OTLP_ENDPOINT 启用 OTLP；OTEL_CONSOLE=1 启用控制台
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(Telemetry.ServiceName, serviceVersion: Telemetry.ServiceVersion));
otel.WithTracing(tracing =>
{
    tracing.AddSource(Telemetry.SourceName)
        .AddSource(Telemetry.MeaiName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation();
    if (Telemetry.OtlpConfigured) tracing.AddOtlpExporter();
    if (Telemetry.ConsoleEnabled) tracing.AddConsoleExporter();
});
otel.WithMetrics(metrics =>
{
    metrics.AddMeter(Telemetry.MeterName)
        .AddMeter(Telemetry.MeaiName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation();
    if (Telemetry.OtlpConfigured) metrics.AddOtlpExporter();
    if (Telemetry.ConsoleEnabled) metrics.AddConsoleExporter();
});
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    if (Telemetry.OtlpConfigured) logging.AddOtlpExporter();
    if (Telemetry.ConsoleEnabled) logging.AddConsoleExporter();
});
// EF Core 的 SQL 日志量大，仅在告警及以上级别进入 OTel 导出
builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // 模式以 EF 模型为唯一事实源（项目未上线：不做迁移/补表/补列；本地库与模型不一致时直接删除 db 文件重建）
    db.Database.EnsureCreated();
}

app.MapProviders();
app.MapExchanges();
app.MapStrategies();
app.MapTraders();

app.Run();
