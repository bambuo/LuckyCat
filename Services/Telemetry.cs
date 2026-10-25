using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LuckyCat.Services;

/// <summary>
/// OpenTelemetry 统一出口：ActivitySource（trader.cycle / ai.call / exchange.fetch）、Meter（决策/耗时/token/行情失败与重试）
/// 与导出开关。导出端按环境变量启用（避免无收集端时的噪声与开销）：
/// - OTLP：设置 OTEL_EXPORTER_OTLP_ENDPOINT（SDK 标准变量，默认本地 4317）即启用；
/// - 控制台：设置 OTEL_CONSOLE=1 启用（本地调试用）。
/// 基础设施埋点（ASP.NET Core / HttpClient / 运行时指标）经 Program.cs 的 AddOpenTelemetry 注册。
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "luckycat";
    public const string UiName = "招财猫";
    public const string SourceName = "LuckyCat";
    // Microsoft.Extensions.AI（ChatClientBuilder.UseOpenTelemetry）的 ActivitySource 与 Meter 名
    public const string MeaiName = "Microsoft.Extensions.AI";
    public const string MeterName = "LuckyCat";

    public static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(MeterName);

    /// <summary>每周期决策计数（标签：action）。</summary>
    public static readonly Counter<long> Decisions = Meter.CreateCounter<long>("trader.decisions", "1", "交易员每周期决策数");

    /// <summary>周期计数（标签：status=completed|failed|skipped|aborted、trigger；LC-STD-01 §10）。</summary>
    public static readonly Counter<long> Cycles = Meter.CreateCounter<long>("trader.cycles", "1", "周期计数（按终态）");

    /// <summary>AI 调用耗时（毫秒；标签：provider/model）。</summary>
    public static readonly Histogram<double> AiCallDuration = Meter.CreateHistogram<double>("trader.ai.duration", "ms", "AI 调用耗时");

    /// <summary>复盘耗时（毫秒；标签：status=completed|failed|skipped、role=reviewer；LC-STD-01 §10「复盘耗时」）。</summary>
    public static readonly Histogram<double> ReviewDuration = Meter.CreateHistogram<double>("trader.review.duration", "ms", "复盘任务耗时");

    /// <summary>AI token 用量（标签：type=输入/输出）。</summary>
    public static readonly Counter<long> AiTokens = Meter.CreateCounter<long>("trader.ai.tokens", "1", "AI token 用量");

    /// <summary>行情拉取重试次数（标签：exchange）。</summary>
    public static readonly Counter<long> ExchangeRetries = Meter.CreateCounter<long>("exchange.fetch.retries", "1", "行情拉取重试次数");

    /// <summary>行情拉取最终失败数（如实标注「数据缺失」的那些序列；标签：exchange）。</summary>
    public static readonly Counter<long> ExchangeFailures = Meter.CreateCounter<long>("exchange.fetch.failures", "1", "行情拉取最终失败数");

    public static string ServiceVersion => typeof(Telemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public static bool OtlpConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    public static bool ConsoleEnabled => Environment.GetEnvironmentVariable("OTEL_CONSOLE") == "1";
}
