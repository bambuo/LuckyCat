using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Tools;
using LuckyCat.Workflows;
using Microsoft.EntityFrameworkCore;

namespace LuckyCat.Services;

/// <summary>
/// 一次复盘的结果（LC-STD-03 §6 复盘错误码由端点层映射：409 <c>review_in_progress</c>、
/// 422 <c>review_no_data</c>；<c>stage_failed</c> / <c>stage_contract_violation</c> /
/// <c>tool_budget_exceeded</c> 为失败类阶段码）。
/// <para>
/// <see cref="Status"/> 取 <see cref="ReviewRunner"/> 的状态字面量：<c>completed</c> / <c>failed</c> /
/// <c>skipped</c> 与 <see cref="Models.AgentStageRun.Status"/> 同一枚举（LC-STD-03 §2.1）；
/// <c>in_progress</c> 只存在于结果层——同一交易员复盘进行中时本次触发直接拒绝、
/// <b>不产生任何阶段行</b>，与 <c>cycle_in_progress</c>「不创建周期行」同一口径。
/// </para>
/// </summary>
public sealed record ReviewRunResult(
    string Status,
    string? ErrorCode,
    string? StageRunId,
    long ElapsedMs,
    DateTime? PeriodStartUtc,
    DateTime? PeriodEndUtc)
{
    /// <summary>本次触发被同交易员的进行中复盘拒绝（不排队、不并行、不落库）。</summary>
    public static ReviewRunResult InProgress(long elapsedMs) =>
        new(ReviewRunner.InProgressStatus, ReviewRunner.InProgressCode, null, elapsedMs, null, null);
}

/// <summary>
/// 复盘宿主（LC-STD-01 §9 复盘任务表全部七行 + §1.35 只读纪律 + §10 复盘耗时可观测）：
/// 一个交易员一次复盘 = 窗口取数探针 → 复盘员只读工具循环 → 结构化经验 → 短事务落库
/// （<see cref="AgentStageRun"/> role=reviewer + <see cref="TraderLesson"/>）。
/// <para>
/// 七行口径逐条落位：
/// ① <b>触发</b>——本类只暴露 <see cref="RunAsync"/>，每日本地时刻由
/// <see cref="Memory.ReviewScheduler"/> 派发，手动端点（Task 14）与本类是同一入口；
/// ② <b>窗口</b>——起点 = 上次<b>成功</b>复盘之后（持久证据 = 最新一份 <see cref="TraderLesson"/> 的
/// <c>PeriodEnd</c>，重启安全），并按 <c>review.maxLookbackDays</c> 裁剪，硬上限
/// <see cref="MaxLookbackDays"/> 天（§9「最多 30 天」，配置放大也不越界）；
/// ③ <b>跳过</b>——严格「窗口内无已平仓交易<b>且</b>无周期」，跳过不调模型、不写经验，
/// 只留一条 <c>skipped</c> 阶段行（<c>review_no_data</c>）供复盘页展示（R-13-3）；
/// ④ <b>输入</b>——已平仓交易 / 周期 / 已有经验三件读工具（§9 输入里的「提案/裁决/执行结果」经
/// 周期 ID 与 <c>get_closed_trades</c> 的盈亏、手续费字段还原；复盘员工具清单只有
/// <see cref="ToolCatalog.Reviewer"/> 一份，不另起清单）；
/// ⑤ <b>输出</b>——<see cref="TraderLesson"/>，字段上限按 LC-STD-03 §2.1 逐列裁剪
/// （summary ≤2,000 字；rules ≤10 条、每条 ≤200 字）；
/// ⑥ <b>限制</b>——只读工具、不改任何配置；单次超时 <see cref="DefaultTimeoutSeconds"/> 秒、
/// 工具上限 <see cref="DefaultToolLimit"/> 次，是 §9 的<b>硬限制</b>，刻意不取
/// <c>agentBudget</c> 交易配置（R-13-6）；超时用链接 <see cref="CancellationTokenSource"/> 传播，
/// 绝不用 <see cref="Task.WhenAny"/> 遗留未取消任务（LC-STD-01 §6）；
/// ⑦ <b>失败</b>——记录 <c>failed</c> 阶段行、不写经验、不阻塞交易，重试发生在下个触发点
/// （§9 失败行；本类不做阶段内重试，故 <c>attempt</c> 恒为 1）。
/// </para>
/// <para>
/// 并发与事务（R-13-5）：每个交易员一把<b>独立</b>进程内 <see cref="SemaphoreSlim"/>，
/// 与 <see cref="TradingCycleRunner"/> 的周期闸完全无关（本类的构造参数里就没有周期宿主）。
/// <see cref="TraderLesson"/> 只有追加写入、交易周期只读经验（<c>AccountReader.GetLessonsAsync</c>），
/// 因此与交易链路不存在记忆写竞态；仍按标准用短事务：一次 <c>SaveChanges</c> 同时写阶段行与经验行，
/// 取数与模型循环期间不持有任何事务。落库与失败记录一律用 <see cref="CancellationToken.None"/>
/// （取消/超时到达时审计仍要写完，与 <see cref="CycleRecorder"/> 同一口径）。
/// </para>
/// <para>
/// 安全（R-13-7、LC-STD-02 §10）：上游异常原文、URL、密钥、SQL 只进服务端日志
/// （<see cref="LogException"/>）；落库与返回给端点的只有稳定错误码 + 固定中文摘要。
/// 记忆（R-13-8、LC-STD-04 §2 块 6）：复盘员<b>不接</b> <c>TradingMemoryContextProvider</c>，
/// 见 <see cref="ReviewerAgent.New"/>。
/// </para>
/// <para>
/// DI 口径与仓库其他后台宿主一致：<see cref="IServiceScopeFactory"/> + 单例 <see cref="SseEventHub"/>，
/// <see cref="AppDbContext"/>、<see cref="ChatClientFactory"/>、<see cref="ExchangeTradingService"/>
/// 都从「每次复盘一个 scope」解析，单例不捕获 scoped 依赖。
/// </para>
/// </summary>
public class ReviewRunner(IServiceScopeFactory scopeFactory, SseEventHub hub)
{
    /// <summary>单次复盘超时（秒，LC-STD-01 §9「限制」行字面值；R-13-6 硬限制）。</summary>
    public const int DefaultTimeoutSeconds = CycleBudget.ReviewTimeoutSeconds;

    /// <summary>单次复盘工具上限（次，LC-STD-01 §9「限制」行字面值；R-13-6 硬限制）。</summary>
    public const int DefaultToolLimit = CycleBudget.ReviewToolLimit;

    // 状态字面量（与 LC-STD-03 §2.1 AgentStageRun.status 枚举一致；in_progress 只在结果层出现）。
    public const string CompletedStatus = "completed";
    public const string FailedStatus = "failed";
    public const string SkippedStatus = "skipped";
    public const string InProgressStatus = "in_progress";

    // 错误码（LC-STD-03 §6：复盘域 review_no_data / review_in_progress 为既有码；
    // stage_failed / stage_contract_violation / tool_budget_exceeded 复用周期域既有码，不新增码）。
    internal const string StageFailedCode = "stage_failed";
    internal const string ContractViolationCode = "stage_contract_violation";
    internal const string NoDataCode = "review_no_data";
    internal const string InProgressCode = "review_in_progress";
    internal const string ToolBudgetCode = ToolErrors.ToolBudgetExceeded;

    /// <summary>复盘窗口上限（天，LC-STD-01 §9「最多 30 天」硬上限）。</summary>
    private const int MaxLookbackDays = 30;

    // TraderLesson 列上限（LC-STD-03 §2.1）。
    private const int MaxSummaryChars = 2_000;
    private const int MaxRuleCount = 10;
    private const int MaxRuleChars = 200;

    // strengths / mistakes / rules / evidence 的条数上限：标准只规定了 rules（≤10 条、每条 ≤200 字）
    // 与 summary（≤2,000 字），其余取有界值防正文膨胀（单条 ≤500 字，与经验注入的字级预算同口径；
    // evidence 按输出契约是「订单/周期 ID 与关键数据」，不得裁到只剩半个 ID 前缀那么短）。
    private const int MaxListItemCount = 20;
    private const int MaxListItemChars = 500;

    // 取数探针上限与只读工具一致（AccountTools 的 ClosedTradeLimit / CycleLimit）。
    private const int ClosedTradeProbeLimit = 50;
    private const int CycleProbeLimit = 20;

    /// <summary>阶段行必填快照列在「复盘尚未装配成功」时的如实标注（不伪造提供商快照）。</summary>
    private const string SnapshotUnavailable = "未取到（复盘在装配前失败）";

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

    /// <summary>
    /// 本次复盘的超时秒数（装配缝）：生产取 §9 硬限制常量，测试可缩短以覆盖超时路径；
    /// 刻意<b>不</b>读策略配置——交易预算改配置不得改回复盘口径（R-13-6）。
    /// </summary>
    protected virtual int TimeoutSeconds => DefaultTimeoutSeconds;

    /// <summary>
    /// 账户只读装配缝（反 IDOR）：复盘与交易周期用<b>同一套绑定范围</b>
    /// <c>(account, traderId, strategyId, memoryFingerprint)</c> 构造 <see cref="AccountReader"/>——
    /// 复盘员能看到的数据上界恰好等于该交易员自己在周期里能看到的上界，且不接受任何调用方归属参数；
    /// 记忆指纹只影响提案记忆（复盘员工具集里没有该工具），传入当前指纹仅为保持范围构造一致。
    /// </summary>
    protected virtual IAccountReader CreateReader(IServiceProvider services, Trader trader,
        ExchangeAccount account, StrategyConfig config, string memoryFingerprint) =>
        new AccountReader(services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<ExchangeTradingService>(),
            new AccountReaderScope(account, trader.Id, trader.StrategyId, config, memoryFingerprint));

    /// <summary>
    /// 运行一次复盘（§9 触发行的唯一执行入口）。同一交易员已有复盘在跑时直接拒绝
    /// （不排队、不并行写同一交易员的记忆，LC-STD-01 §9 末行），返回 <c>in_progress</c> 且不落任何行。
    /// </summary>
    public async Task<ReviewRunResult> RunAsync(Trader trader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trader);
        var gate = _gates.GetOrAdd(trader.Id, _ => new SemaphoreSlim(1, 1));
        if (await gate.WaitAsync(0, CancellationToken.None))
        {
            try
            {
                return await RunOwnedAsync(trader, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        Console.WriteLine($"[Review] 交易员 #{trader.Id} 的复盘正在进行，本次触发按 {InProgressCode} 拒绝");
        return ReviewRunResult.InProgress(0);
    }

    private async Task<ReviewRunResult> RunOwnedAsync(Trader trader, CancellationToken ct)
    {
        var stageRunId = Ids.NewUlid();
        var started = Stopwatch.StartNew();
        // §9「单次超时 120s」覆盖整次复盘（取数探针 + 模型工具循环 + 落库）；
        // 超时一律经链接 CTS 传播到模型与工具循环（LC-STD-01 §6 禁止 Task.WhenAny 遗留未取消任务）。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var runToken = timeout.Token;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        StrategyConfig? config = null;
        Provider? provider = null;
        try
        {
            config = await LoadConfigAsync(db, trader, runToken);
            provider = await db.Providers.FindAsync([trader.ProviderId], runToken)
                       ?? throw new InvalidOperationException("模型提供商不存在");
            var account = await db.ExchangeAccounts.FindAsync([trader.ExchangeAccountId], runToken)
                          ?? throw new InvalidOperationException("交易所账户不存在");

            return await ReviewCoreAsync(scope.ServiceProvider, db, trader, account, config, provider,
                stageRunId, runToken, started);
        }
        catch (Exception ex)
        {
            // 模型/上游异常、绑定缺失、取数抛错都归这一条路径：原始文本只进日志（R-13-7）。
            LogException($"trader:{trader.Id} 复盘异常", ex);
            var timedOut = timeout.IsCancellationRequested && !ct.IsCancellationRequested;
            var summary = timedOut
                ? $"复盘阶段超时（超过 {TimeoutSeconds} 秒未返回），本轮未产出经验"
                : ct.IsCancellationRequested
                    ? "复盘已取消，本轮未产出经验"
                    : "复盘阶段执行失败，本轮未产出经验";
            return await FailAsync(db, trader, stageRunId, config, provider, StageFailedCode, summary,
                started, stats: null);
        }
    }

    private async Task<ReviewRunResult> ReviewCoreAsync(IServiceProvider services, AppDbContext db, Trader trader,
        ExchangeAccount account, StrategyConfig config, Provider provider, string stageRunId,
        CancellationToken runToken, Stopwatch started)
    {
        // 窗口两端同源：periodEnd 一旦取值就不再刷新，避免 span 比配置上限多跑出一段。
        var periodEnd = DateTime.UtcNow;
        var periodStart = await WindowStartAsync(db, trader, config, periodEnd, runToken);
        var reader = CreateReader(services, trader, account, config, PromptLibrary.MemoryFingerprint(config));

        // §9 跳过的判据就是本步探针：两个「无」同时成立才算无数据，宿主不额外问模型。
        var trades = await reader.GetClosedTradesAsync(periodStart, ClosedTradeProbeLimit, runToken);
        var cycles = await reader.GetCyclesAsync(periodStart, CycleProbeLimit, runToken);
        var instructions = PromptLibrary.Build(AgentRole.Reviewer, config);
        if (trades.Count == 0 && cycles.Count == 0)
        {
            return await SkipAsync(db, trader, stageRunId, instructions, provider, periodStart, periodEnd, started);
        }

        var lessonCount = await db.TraderLessons.CountAsync(lesson => lesson.TraderId == trader.Id, runToken);
        var budget = CycleBudget.ForReview();
        // 复盘不是周期：审计器自带 cycleId=null（LC-STD-03 §2.1「复盘工具调用 cycleId 为 null」），
        // 工具清单仍只取 ToolRegistry.BuildReviewerTools 这一个装配位。
        var audit = new ToolAuditor(db, hub, budget, cycleId: null, stageRunId, SseChannels.Trader(trader.Id));
        var tools = ToolRegistry.BuildReviewerTools(reader, audit);
        // 提供商客户端与周期宿主同一口径：本次复盘作用域内创建、退出即释放（LC-STD-01 §4.2 不跨轮复用）。
        using var client = services.GetRequiredService<ChatClientFactory>().Create(provider);
        var agent = ReviewerAgent.New(client, config, tools);
        var prompt = ReviewerAgent.BuildUserPrompt(new ReviewWindow(trader.Id, trader.Name, periodStart, periodEnd,
            trades.Count, cycles.Count, lessonCount));

        var (report, response) = await new AgentRunner().RunAsync<ReviewReport>(agent, prompt, runToken);
        var stats = StageRunStats.From(response.Messages, response.Usage, started.ElapsedMilliseconds);

        // 工具上限耗尽是<b>失败</b>而不是「拿一个 tool_budget_exceeded 信封继续拼经验」（R-13-6）：
        // CycleBudget.ToolRefusals 记录被拒次数，这里据此判失败，先于契约校验（预算耗尽时输出必然不完整）。
        if (budget.ToolRefusals > 0)
        {
            return await FailAsync(db, trader, stageRunId, config, provider, ToolBudgetCode,
                $"复盘工具调用已达上限（{DefaultToolLimit} 次），本轮未产出经验", started, stats);
        }

        // 结构化输出契约（§9 输出行 / LC-STD-03 §2.1）：没有摘要就没有可注入的经验。
        if (report is null || string.IsNullOrWhiteSpace(report.Summary))
        {
            return await FailAsync(db, trader, stageRunId, config, provider, ContractViolationCode,
                "复盘结构化输出缺少摘要，本轮未产出经验", started, stats);
        }

        return await CompleteAsync(db, trader, stageRunId, provider, instructions, report, stats,
            periodStart, periodEnd, started);
    }

    /// <summary>
    /// 窗口起点 = 上次<b>成功</b>复盘之后，最多 <see cref="MaxLookbackDays"/> 天（§9 窗口行）。
    /// 水位只取持久数据：最新一份 <see cref="TraderLesson"/> 的 <c>PeriodEnd</c>——经验永久保留
    /// （LC-STD-03 §2.3），而阶段行/工具明细 90 天清理，不能作为窗口水位；进程内状态一律不参与判定，
    /// 因此重启后同一窗口不会重跑（R-13-2）。无失败阶段行参与的语义：失败复盘不产出经验，
    /// 其窗口自然并入下一次窗口，§9「下个触发点重试」不丢数据。
    /// </summary>
    private static async Task<DateTime> WindowStartAsync(AppDbContext db, Trader trader, StrategyConfig config,
        DateTime periodEnd, CancellationToken ct)
    {
        var lastWatermark = await db.TraderLessons.AsNoTracking()
            .Where(lesson => lesson.TraderId == trader.Id)
            .OrderByDescending(lesson => lesson.CreatedAt)
            .ThenByDescending(lesson => lesson.Id)
            .Select(lesson => lesson.PeriodEnd)
            .FirstOrDefaultAsync(ct);

        var configured = config.Review.MaxLookbackDays;
        var lookbackDays = Math.Clamp(configured > 0 ? configured : MaxLookbackDays, 1, MaxLookbackDays);
        var floor = periodEnd.AddDays(-lookbackDays);
        return lastWatermark > floor ? lastWatermark : floor;
    }

    /// <summary>
    /// 成功路径：一条 <c>completed</c> 阶段行 + 一份 <see cref="TraderLesson"/> 在同一次
    /// <c>SaveChanges</c> 里写入（短事务，§9 末行）；正文逐列裁剪到 LC-STD-03 §2.1 上限。
    /// </summary>
    private async Task<ReviewRunResult> CompleteAsync(AppDbContext db, Trader trader, string stageRunId,
        Provider provider, string instructions, ReviewReport report, StageRunStats stats,
        DateTime periodStart, DateTime periodEnd, Stopwatch started)
    {
        var summary = Clamp(OneLine(report.Summary), MaxSummaryChars);
        var strengths = ClampItems(report.Strengths, MaxListItemChars);
        var mistakes = ClampItems(report.Mistakes, MaxListItemChars);
        var rules = ClampItems(report.Rules, MaxRuleChars, MaxRuleCount);
        var evidence = ClampItems(report.Evidence, MaxListItemChars);
        var persisted = new ReviewReport(summary, strengths, mistakes, rules, evidence);
        var elapsed = started.ElapsedMilliseconds;
        var toolCalls = await CountToolCallsAsync(db, stageRunId);
        var (transcript, truncated) = CycleRecorder.BuildTranscript(stats.Messages);
        var createdAt = DateTime.UtcNow;

        AddStageRun(db, new AgentStageRun
        {
            Id = stageRunId,
            CycleId = null,
            TraderId = trader.Id,
            Role = ToolRegistry.ReviewerRoleName,
            Attempt = 1,
            Status = CompletedStatus,
            ProviderName = provider.Name,
            Model = provider.Model,
            Instructions = instructions,
            TranscriptJson = transcript,
            OutputJson = JsonSerializer.Serialize(persisted, PromptJson.Options),
            Reasoning = stats.Reasoning,
            InputTokens = stats.InputTokens,
            OutputTokens = stats.OutputTokens,
            ElapsedMs = elapsed,
            ToolCallCount = toolCalls,
            Truncated = truncated,
            CreatedAt = createdAt,
        });
        db.TraderLessons.Add(new TraderLesson
        {
            TraderId = trader.Id,
            StageRunId = stageRunId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Summary = summary,
            StrengthsJson = JsonSerializer.Serialize(strengths, PromptJson.Options),
            MistakesJson = JsonSerializer.Serialize(mistakes, PromptJson.Options),
            RulesJson = JsonSerializer.Serialize(rules, PromptJson.Options),
            EvidenceJson = JsonSerializer.Serialize(evidence, PromptJson.Options),
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        RecordDuration(trader, elapsed, CompletedStatus, toolCalls);
        Console.WriteLine($"[Review] trader:{trader.Id} 复盘完成 窗口 {periodStart:O}~{periodEnd:O} " +
                          $"经验 1 份 工具 {toolCalls} 次 耗时 {elapsed}ms");
        return new ReviewRunResult(CompletedStatus, null, stageRunId, elapsed, periodStart, periodEnd);
    }

    /// <summary>
    /// 跳过路径（R-13-3）：不调模型、不写经验，只留一条 <c>skipped</c> 阶段行 +
    /// <c>review_no_data</c>，Task 17 的复盘页据此展示「本轮无数据」；
    /// 交易员状态列一律不碰（§1.35：复盘不得修改交易员状态）。
    /// </summary>
    private async Task<ReviewRunResult> SkipAsync(AppDbContext db, Trader trader, string stageRunId,
        string instructions, Provider provider, DateTime periodStart, DateTime periodEnd, Stopwatch started)
    {
        var elapsed = started.ElapsedMilliseconds;
        AddStageRun(db, new AgentStageRun
        {
            Id = stageRunId,
            CycleId = null,
            TraderId = trader.Id,
            Role = ToolRegistry.ReviewerRoleName,
            Attempt = 1,
            Status = SkippedStatus,
            ProviderName = provider.Name,
            Model = provider.Model,
            Instructions = instructions,
            TranscriptJson = "[]",
            ElapsedMs = elapsed,
            ToolCallCount = 0,
            Error = Truncate($"{NoDataCode}：复盘窗口内无已平仓交易且无周期，本轮未产出经验", 1000),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        RecordDuration(trader, elapsed, SkippedStatus, 0);
        Console.WriteLine($"[Review] trader:{trader.Id} 窗口 {periodStart:O}~{periodEnd:O} 无数据，跳过复盘");
        return new ReviewRunResult(SkippedStatus, NoDataCode, stageRunId, elapsed, periodStart, periodEnd);
    }

    /// <summary>
    /// 失败路径（§9 失败行）：写一条 <c>failed</c> 阶段行——稳定错误码 + 固定中文摘要（R-13-7），
    /// 不写经验、不改交易员状态、不阻塞交易；重试发生在下个触发点（§9 窗口从上次成功复盘起算，不丢数据）。
    /// </summary>
    private async Task<ReviewRunResult> FailAsync(AppDbContext db, Trader trader, string stageRunId,
        StrategyConfig? config, Provider? provider, string code, string summary, Stopwatch started,
        StageRunStats? stats)
    {
        var elapsed = started.ElapsedMilliseconds;
        var toolCalls = await CountToolCallsAsync(db, stageRunId);
        var transcript = "[]";
        var truncated = false;
        if (stats is not null)
        {
            (transcript, truncated) = CycleRecorder.BuildTranscript(stats.Messages);
        }

        AddStageRun(db, new AgentStageRun
        {
            Id = stageRunId,
            CycleId = null,
            TraderId = trader.Id,
            Role = ToolRegistry.ReviewerRoleName,
            // 阶段内不做契约重试（§9 的重试语义是「下个触发点」），因此恒为 1。
            Attempt = 1,
            Status = FailedStatus,
            ProviderName = provider?.Name ?? SnapshotUnavailable,
            Model = provider?.Model ?? SnapshotUnavailable,
            // 系统在装配前失败（策略配置读不到）时无指令原文可落，如实标注而非伪造一份指令。
            Instructions = config is null
                ? SnapshotUnavailable
                : PromptLibrary.Build(AgentRole.Reviewer, config),
            TranscriptJson = transcript,
            Reasoning = stats?.Reasoning,
            InputTokens = stats?.InputTokens,
            OutputTokens = stats?.OutputTokens,
            ElapsedMs = elapsed,
            ToolCallCount = toolCalls,
            Truncated = truncated,
            Error = Truncate($"{code}：{summary}", 1000),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        RecordDuration(trader, elapsed, FailedStatus, toolCalls);
        Console.WriteLine($"[Review] trader:{trader.Id} 复盘失败 {code} 耗时 {elapsed}ms");
        return new ReviewRunResult(FailedStatus, code, stageRunId, elapsed, null, null);
    }

    /// <summary>本阶段已落库的工具调用行数（与 ToolAuditor 的 stageRunId 同源，§10 工具计数口径）。</summary>
    private static Task<int> CountToolCallsAsync(AppDbContext db, string stageRunId) =>
        db.ToolInvocations.CountAsync(invocation => invocation.StageRunId == stageRunId, CancellationToken.None);

    /// <summary>
    /// 幂等写阶段行：成功路径的 <c>SaveChanges</c> 若在插入阶段行之后抛错（数据库不可用等），
    /// 紧随其后的失败补写会在<b>同一个 ChangeTracker</b> 里看到这个 <c>Id</c> 已被跟踪；
    /// 此时只改写<b>尚未提交</b>的那份（<see cref="EntityState.Added"/>），而不是再 <c>Add</c> 一份——
    /// 否则 EF 直接抛 identity conflict，把「记录失败」这件事本身也弄丢（§9 失败行要求失败必须留痕）。
    /// 已经提交成功的终态行一律不重写：复盘的经验与阶段行是既成事实，不能被随后的宿主异常改成 failed。
    /// </summary>
    private static void AddStageRun(AppDbContext db, AgentStageRun row)
    {
        foreach (var entry in db.ChangeTracker.Entries<AgentStageRun>())
        {
            if (entry.Entity.Id != row.Id) continue;
            if (entry.State == EntityState.Added) entry.CurrentValues.SetValues(row);
            return;
        }

        db.AgentStageRuns.Add(row);
    }
    /// <summary>复盘耗时（LC-STD-01 §10「复盘耗时」）：标签只有状态/角色/工具次数，不含账户与密钥。</summary>
    private static void RecordDuration(Trader trader, long elapsedMs, string status, int toolCalls)
    {
        using var activity = Telemetry.Source.StartActivity("review.run");
        activity?.SetTag("trader.id", trader.Id);
        activity?.SetTag("review.status", status);
        Telemetry.ReviewDuration.Record(elapsedMs,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("role", ToolRegistry.ReviewerRoleName),
            new KeyValuePair<string, object?>("tool.calls", toolCalls));
    }

    private static async Task<StrategyConfig> LoadConfigAsync(AppDbContext db, Trader trader, CancellationToken ct)
    {
        var strategy = await db.Strategies.FindAsync([trader.StrategyId], ct)
                       ?? throw new InvalidOperationException("策略不存在");
        return JsonSerializer.Deserialize<StrategyConfig>(strategy.ConfigJson, VerificationJson.Options)
               ?? new StrategyConfig();
    }

    private static void LogException(string context, Exception? ex)
    {
        // 上游异常原文只进服务端日志，绝不进入落库数据或 SSE 负载（R-13-7、LC-STD-02 §10）。
        if (ex is null) return;
        Console.WriteLine($"[Review] {context}：{ex}");
    }

    /// <summary>折行 + 去首尾空白：自由文本一律单行入库，避免换行放大注入正文。</summary>
    private static string OneLine(string? text) => (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static List<string> ClampItems(List<string>? items, int maxChars, int maxCount = MaxListItemCount) =>
        (items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(OneLine)
            .Where(item => item.Length > 0)
            .Take(maxCount)
            .Select(item => Clamp(item, maxChars))
            .ToList();

    private static string Clamp(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars];

    private static string? Truncate(string? value, int maxChars) =>
        value is null || value.Length <= maxChars ? value : value[..maxChars];
}
