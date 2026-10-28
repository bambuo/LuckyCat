using Microsoft.AspNetCore.Builder;
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
using LuckyCat.Services.NewsSources;

namespace LuckyCat.Hosting;

public static class LuckyCatHost
{
    public static WebApplication Build(string[] args, LuckyCatHostOptions? options = null)
    {
        options ??= LuckyCatHostOptions.Development();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = options.ContentRootPath,
        });

        // 替换原来的硬编码 UseUrls。
        builder.WebHost.UseUrls(options.Urls);

        // 替换原来的 UseSqlite("Data Source=luckycat.db")。
        builder.Services.AddDbContext<AppDbContext>(db =>
            db.UseSqlite($"Data Source={options.DatabasePath}"));
        builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
        {
            // 请求绑定与结果序列化统一对齐 ApiJson 约定（LC-STD-03 §1.1/§1.2）：
            // camelCase 属性与 camelCase 枚举字符串；请求体禁止未映射字段（Disallow → 400 validation_failed）。
            // P5/P6 端点重写（Task 14）起生效；旧前端与新契约同步适配。
            ApiJson.Configure(jsonOptions.SerializerOptions);
        });
        builder.Services.AddScoped<ChatClientFactory>();
        builder.Services.AddScoped<VerificationRunner>();
        builder.Services.AddScoped<ExchangeVerificationRunner>();
        builder.Services.AddScoped<MarketSnapshotService>();
        builder.Services.AddScoped<CoinRankingService>();
        builder.Services.AddScoped<MarketSentimentService>();
        // 内置新闻源（Task 14.5/14.6/14.7）：都只依赖单例 IHttpClientFactory，自身仅持本地 TTL 缓存，不俘获 HttpClient。
        // 聚合器 scoped；注册顺序即 IEnumerable<INewsSource> 顺序，也是 get_news 轮转取条的首轮顺序
        // （limit 上限 12，内置源 11 个 → 首轮 11 个全部出场，第 12 行回到第一家继续）：
        // 金十 → 链捕手（最及时的中文快讯）→ PANews → Binance/OKX 公告 → 财经日历 → 美联储 → 四家英文媒体。
        // 财经日历没有事件时返回 Problem、不占轮转名额。
        builder.Services.AddSingleton<Jin10NewsSource>();
        builder.Services.AddSingleton<ChainCatcherNewsSource>();
        builder.Services.AddSingleton<INewsSource>(sp => sp.GetRequiredService<Jin10NewsSource>());
        builder.Services.AddSingleton<INewsSource>(sp => sp.GetRequiredService<ChainCatcherNewsSource>());
        // 通用 RSS 适配器：一个类 + 每源一条注册；PANews 301、CoinDesk 308 由 HttpClient 默认跟随重定向。
        builder.Services.AddSingleton<INewsSource>(sp => new RssNewsSource(
            "PANews", "https://rss.panewslab.com/zh/gtimg/rss", sp.GetRequiredService<IHttpClientFactory>()));
        builder.Services.AddSingleton<BinanceAnnouncementsNewsSource>();
        builder.Services.AddSingleton<INewsSource>(sp => sp.GetRequiredService<BinanceAnnouncementsNewsSource>());
        builder.Services.AddSingleton<OkxAnnouncementsNewsSource>();
        builder.Services.AddSingleton<INewsSource>(sp => sp.GetRequiredService<OkxAnnouncementsNewsSource>());
        builder.Services.AddSingleton<ForexCalendarNewsSource>();
        builder.Services.AddSingleton<INewsSource>(sp => sp.GetRequiredService<ForexCalendarNewsSource>());
        builder.Services.AddSingleton<INewsSource>(sp => new RssNewsSource(
            "美联储新闻稿", "https://www.federalreserve.gov/feeds/press_all.xml", sp.GetRequiredService<IHttpClientFactory>()));
        builder.Services.AddSingleton<INewsSource>(sp => new RssNewsSource(
            "Cointelegraph", "https://cointelegraph.com/rss", sp.GetRequiredService<IHttpClientFactory>()));
        builder.Services.AddSingleton<INewsSource>(sp => new RssNewsSource(
            "CoinDesk", "https://www.coindesk.com/arc/outboundfeeds/rss/", sp.GetRequiredService<IHttpClientFactory>()));
        builder.Services.AddSingleton<INewsSource>(sp => new RssNewsSource(
            "The Block", "https://www.theblock.co/rss.xml", sp.GetRequiredService<IHttpClientFactory>()));
        builder.Services.AddSingleton<INewsSource>(sp => new RssNewsSource(
            "Decrypt", "https://decrypt.co/feed", sp.GetRequiredService<IHttpClientFactory>()));
        builder.Services.AddScoped<CryptoNewsAggregator>();
        builder.Services.AddScoped<ExchangeTradingService>();
        // P4 周期宿主切流（Task 14 B / R-14-5）：旧 TraderRunnerService 的宿主与旧单发演练链路已在
        // Task 18（P7）整体删除——本进程恰好一条交易调度链（TradingCycleRunner）。
        // TradingCycleRunner 仍是可解析单例而非 hosted 服务：调度循环只经端点 start 与
        // CycleStartupRecovery（先 RecoverAsync 收敛遗留 running，再为 IsRunning 交易员 Start）驱动。
        builder.Services.AddScoped<CycleRecorder>();
        builder.Services.AddSingleton<TradingCycleRunner>();
        builder.Services.AddHostedService<CycleStartupRecovery>();
        // P5 复盘（Task 13）：复盘宿主 + 每日本地时刻调度。
        // 与 R-11-6 的关系：那条禁令针对的是迁移期不得把**交易**宿主（TradingCycleRunner）注册为 hosted 调度。
        // 复盘员不下单、不改策略/风控/交易员状态（LC-STD-01 §1.35、§9「只读工具；不修改任何配置」），
        // 因此 ReviewScheduler 不构成第二条交易调度链；而 §9 的「本地时刻（默认 00:10）」定时触发
        // 只能由宿主驱动，这里必须托管。它与交易周期各用各的闸，互不占用（R-13-5）。
        builder.Services.AddSingleton<ReviewRunner>();
        builder.Services.AddHostedService<ReviewScheduler>();
        builder.Services.AddSingleton<PositionHistorySyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PositionHistorySyncService>());
        // 数据保留（LC-STD-03 §2.5）：每日本地 03:30 清理 90 天前的阶段/工具明细；
        // 只删审计明细，不触交易数据，与交易调度链无关。
        builder.Services.AddHostedService<DataRetentionService>();
        builder.Services.AddSingleton<AccountStreamService>();
        builder.Services.AddSingleton<SseEventHub>();
        // 统一弹性管道（情绪/新闻等所有经 HttpClientFactory 的请求）：突发超时最多重试 3 次、线性退避 1/2/3 秒
        builder.Services.AddHttpClient();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(resilience =>
            {
                resilience.Retry.MaxRetryAttempts = 3;
                resilience.Retry.Delay = TimeSpan.FromSeconds(1);
                resilience.Retry.BackoffType = DelayBackoffType.Linear;
                resilience.Retry.UseJitter = false;
                resilience.Retry.OnRetry = retryArgs =>
                {
                    var reason = retryArgs.Outcome.Exception?.GetType().Name
                        ?? retryArgs.Outcome.Result?.StatusCode.ToString()
                        ?? "失败";
                    Console.WriteLine($"[Http] {retryArgs.Outcome.Result?.RequestMessage?.RequestUri?.Host ?? "请求"} 第 {retryArgs.AttemptNumber + 1} 次重试（{reason}）");
                    return ValueTask.CompletedTask;
                };
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
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

        // Task 2 会在此处插入静态托管：
        // if (options.ServeStaticFiles) StaticFrontend.UseLuckyCatFrontend(app, options.WebRootPath);

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
        app.MapCycles();
        app.MapHealth();

        // Task 2 会在此处插入 SPA 回退：
        // if (options.ServeStaticFiles) StaticFrontend.MapLuckyCatFrontend(app, options.WebRootPath);

        return app;
    }
}
