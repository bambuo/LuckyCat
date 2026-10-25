using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Endpoints;
using LuckyCat.Memory;
using LuckyCat.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5210");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=luckycat.db"));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // 请求绑定与结果序列化统一对齐 ApiJson 约定（LC-STD-03 §1.1/§1.2）：
    // camelCase 属性与 camelCase 枚举字符串；请求体禁止未映射字段（Disallow → 400 validation_failed）。
    // P5/P6 端点重写（Task 14）起生效；旧前端与新契约同步适配。
    ApiJson.Configure(options.SerializerOptions);
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
// P4 周期宿主（Task 11）迁移期（到 Task 18 删除旧 TraderRunnerService 为止）：
// TradingCycleRunner 只注册为可解析单例，**不得**注册为 hosted 调度（R-11-6）——旧 TraderRunnerService
// 仍是唯一自动调度链，双宿主会对同一 IsRunning 交易员起两条循环、实盘双重开仓。
// 启动恢复由 CycleStartupRecovery 单独承担（只标记遗留 running 周期，不起任何循环）。
// Task 18 切流步骤：把 TradingCycleRunner 重新 AddHostedService、删除 CycleStartupRecovery 与旧链路；
// 切流前请勿为 TradingCycleRunner 恢复任何 hosted 注册。
builder.Services.AddScoped<CycleRecorder>();
builder.Services.AddSingleton<TradingCycleRunner>();
builder.Services.AddHostedService<CycleStartupRecovery>();
// P5 复盘（Task 13）：复盘宿主 + 每日本地时刻调度。
// 与 R-11-6 的关系：那条禁令针对的是迁移期不得把**交易**宿主（TradingCycleRunner）注册为 hosted 调度
// ——旧 TraderRunnerService 仍是唯一自动调度链，双宿主会对同一 IsRunning 交易员双重开仓。
// 复盘员不下单、不改策略/风控/交易员状态（LC-STD-01 §1.35、§9「只读工具；不修改任何配置」），
// 因此 ReviewScheduler 不构成第二条交易调度链；而 §9 的「本地时刻（默认 00:10）」定时触发
// 只能由宿主驱动，这里必须托管。它与交易周期各用各的闸，互不占用（R-13-5）。
builder.Services.AddSingleton<ReviewRunner>();
builder.Services.AddHostedService<ReviewScheduler>();
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

// 未预期异常与请求体绑定失败的统一包裹（LC-STD-03 §1.2/§6；响应不含堆栈/SQL/上游地址，LC-STD-02）
app.UseApiErrorWrapper();

app.MapProviders();
app.MapExchanges();
app.MapStrategies();
app.MapTraders();
app.MapHealth();

app.Run();
