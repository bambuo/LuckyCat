using System.Globalization;
using System.Text;
using System.Text.Json;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace LuckyCat.Services;

/// <summary>
/// 周期落库记录器（LC-STD-01 §4.1、LC-STD-03 §2.1/§4.2）：把 <see cref="TradingCycle"/>、
/// <see cref="AgentStageRun"/>、<see cref="TradeProposal"/>、<see cref="RiskVerdict"/> 写入本周期 DI 作用域的
/// <see cref="AppDbContext"/>（一次写一次提交 = 短事务），并按约定负载向
/// <see cref="TradingCycleContext.SseChannel"/>（频道单点决策：试运行按周期独立频道，其余按交易员频道）
/// 推送 <c>cycle</c> / <c>stage</c> / <c>proposal</c> / <c>verdict</c>。
/// <para>
/// 三条硬约束：
/// ① 阶段行的 Id 复用执行器的 <c>StageRunId</c>，与 ToolAuditor 落的 ToolInvocation.stageRunId 同源；
/// ② 终态写入是幂等的——幂等位只在终态落库成功后置位，置位后任何再次调用都是空操作，
///    因此宿主的 <c>finally</c> 守卫既能兜底「绝不留下 running」（落库抛错时可重试），又不会覆盖或重复计数终态；
/// ③ 落库与推送一律使用 <see cref="CancellationToken.None"/>：取消/超时到达时审计必须仍然写完
///    （与写工具订单落库同一口径），且只写稳定错误码 + 中文摘要，绝不写上游原始异常文本。
/// </para>
/// </summary>
public sealed class CycleRecorder(AppDbContext db, SseEventHub hub) : ICycleRecorder
{
    /// <summary>AgentStageRun.transcriptJson 上限：128 KiB（LC-STD-01 §6、LC-STD-03 §2.1）。</summary>
    public const int MaxTranscriptBytes = 128 * 1024;

    /// <summary>TradingCycle.strategyConfigJson 上限：64 KiB，超过只拒绝创建、不截断（LC-STD-03 §2.1）。</summary>
    public const int MaxStrategyConfigBytes = 64 * 1024;

    private const string TruncationMarker = "…（消息记录已截断）";

    private const string OmittedTranscriptJson =
        """[{"role":"system","text":"消息记录超过 128 KiB，已整体省略","tools":[]}]""";

    private TradingCycleContext? _ctx;
    private TradingCycle? _cycle;
    private string? _channelKey;
    private string? _proposalId;
    private bool _terminal;
    private bool _terminalEventPublished;
    private int _inputTokens;
    private int _outputTokens;
    private long _startedTimestamp;

    /// <summary>终态是否已写入（宿主的 finally 守卫据此判断是否需要补写）。</summary>
    public bool IsTerminal => _terminal;

    /// <summary>周期当前状态（未写入终态时为 running；供宿主打 trace/metric 标签）。</summary>
    public string? TerminalStatus => _cycle?.Status;

    /// <summary>
    /// 试运行事件频道：按周期隔离（试运行没有真实交易员，多个 test-run 并发时不得互相串流）。
    /// 该频道只服务 test-run 的响应流，不进入交易员看板频道。
    /// </summary>
    public static string TestRunChannel(string cycleId) => $"test-run:{cycleId}";

    /// <summary>
    /// 创建并落库 <c>running</c> 周期（LC-STD-01 §8.2「周期开始前必须先创建并提交，状态 running」），
    /// 并发布 cycle 开始事件。策略配置快照超过 64 KiB 时拒绝创建（不截断）。
    /// </summary>
    public async Task<TradingCycle> StartCycleAsync(TradingCycleContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_cycle is not null) return _cycle;

        var configJson = JsonSerializer.Serialize(ctx.Config, VerificationJson.Options);
        if (Encoding.UTF8.GetByteCount(configJson) > MaxStrategyConfigBytes)
        {
            throw new InvalidOperationException("策略配置快照超过 64 KiB，已拒绝创建周期");
        }

        // 试运行没有真实交易员与交易所账户：两列按契约写 null（LC-STD-03 §2.1/§3.5）。
        var isTest = ctx.Trigger == "test";
        _ctx = ctx;
        _cycle = new TradingCycle
        {
            Id = ctx.CycleId,
            TraderId = isTest ? null : ctx.Trader.Id,
            StrategyId = ctx.Trader.StrategyId,
            ProviderId = ctx.Provider.Id,
            ExchangeAccountId = isTest ? null : ctx.Account.Id,
            Mode = ctx.Mode,
            Trigger = ctx.Trigger,
            Status = "running",
            StartedAt = DateTime.UtcNow,
            StrategyConfigJson = configJson,
            MemoryFingerprint = PromptLibrary.MemoryFingerprint(ctx.Config),
        };
        _startedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _channelKey = ctx.SseChannel;

        db.TradingCycles.Add(_cycle);
        await db.SaveChangesAsync(CancellationToken.None);
        if (!isTest) await TouchTraderAsync(ctx.Trader.Id, "running");
        hub.Publish(_channelKey, new SseEvent("cycle", StartedPayload()));
        return _cycle;
    }

    /// <summary>记录一个成功阶段：AgentStageRun 原文落库 + 提案/裁决落库 + stage/proposal/verdict 事件。</summary>
    public async Task RecordStageAsync(string role, string stageRunId, object output, string instructions,
        StageRunStats stats, int attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(stageRunId);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(stats);
        if (_ctx is not { } ctx || _cycle is null) return;

        var attemptNumber = Math.Max(1, attempt);
        if (await IsRecordedAsync(stageRunId)) return;
        var toolCalls = await db.ToolInvocations.CountAsync(i => i.StageRunId == stageRunId, CancellationToken.None);
        var (transcript, truncated) = BuildTranscript(stats.Messages);

        db.AgentStageRuns.Add(new AgentStageRun
        {
            Id = stageRunId,
            CycleId = ctx.CycleId,
            TraderId = ctx.Trigger == "test" ? null : ctx.Trader.Id,
            Role = role,
            Attempt = attemptNumber,
            Status = "completed",
            ProviderName = ctx.Provider.Name,
            Model = ctx.Provider.Model,
            Instructions = instructions,
            TranscriptJson = transcript,
            OutputJson = JsonSerializer.Serialize(output, AgentJson.Options),
            Reasoning = Truncate(stats.Reasoning, StageRunStats.MaxReasoningChars),
            InputTokens = stats.InputTokens,
            OutputTokens = stats.OutputTokens,
            ElapsedMs = stats.ElapsedMs,
            ToolCallCount = toolCalls,
            Truncated = truncated,
            CreatedAt = DateTime.UtcNow,
        });
        _inputTokens = SaturateAdd(_inputTokens, stats.InputTokens);
        _outputTokens = SaturateAdd(_outputTokens, stats.OutputTokens);
        // 周期预算的 token 累计只发生在宿主装配的每次模型调用闸（TradingCycleRunner.
        // TokenBudgetGatedChatClient，LC-STD-01 §6「每次模型调用前累计检查」）；
        // 这里只累计阶段用量用于 AgentStageRun/TradingCycle 落库列，不再向 CycleBudget 重复计数。

        object? proposalPayload = null;
        VerdictRecordedEvent? verdictPayload = null;
        switch (role)
        {
            case "trader" when output is ProposalMessage proposalMessage:
                proposalPayload = ProposalPayload(RecordProposal(ctx, proposalMessage.Proposal));
                break;
            case "risk" when output is VerdictMessage verdictMessage:
                verdictPayload = RecordVerdict(ctx, verdictMessage);
                break;
        }

        // 先提交，再推送：SSE 只宣告已落库的事实（回滚或写失败不会留下不存在的事件）。
        await db.SaveChangesAsync(CancellationToken.None);
        hub.Publish(_channelKey!, new SseEvent("stage", new StageRecordedEvent(ctx.CycleId, stageRunId, role,
            "completed", attemptNumber, stats.ElapsedMs, stats.InputTokens, stats.OutputTokens, toolCalls,
            SummaryOf(output), null)));
        if (proposalPayload is not null) hub.Publish(_channelKey!, new SseEvent("proposal", proposalPayload));
        if (verdictPayload is not null) hub.Publish(_channelKey!, new SseEvent("verdict", verdictPayload));
    }

    /// <summary>
    /// 记录一个失败阶段（LC-STD-01 §7「写失败阶段」）：由宿主在阶段抛错时调用，
    /// 只写稳定错误码与中文摘要，绝不写入上游异常原文（R-11-1）。
    /// </summary>
    public async Task RecordStageFailureAsync(string role, string stageRunId, string errorCode, string summary,
        CancellationToken ct)
    {
        if (_ctx is not { } ctx || _cycle is null) return;

        if (await IsRecordedAsync(stageRunId)) return;
        var toolCalls = await db.ToolInvocations.CountAsync(i => i.StageRunId == stageRunId, CancellationToken.None);
        db.AgentStageRuns.Add(new AgentStageRun
        {
            Id = stageRunId,
            CycleId = ctx.CycleId,
            TraderId = ctx.Trigger == "test" ? null : ctx.Trader.Id,
            Role = role,
            Attempt = 1,
            Status = "failed",
            ProviderName = ctx.Provider.Name,
            Model = ctx.Provider.Model,
            Instructions = PromptLibrary.Build(RoleOf(role), ctx.Config),
            TranscriptJson = "[]",
            Error = Truncate($"{errorCode}：{summary}", 1000),
            ToolCallCount = toolCalls,
            Truncated = false,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(CancellationToken.None);
        hub.Publish(_channelKey!, new SseEvent("stage", new StageRecordedEvent(ctx.CycleId, stageRunId, role,
            "failed", 1, 0, null, null, toolCalls, null, Truncate($"{errorCode}：{summary}", 500))));
    }

    /// <summary>
    /// 写入周期终态（幂等）。幂等位只在终态<strong>落库成功</strong>后置位：瞬时 DB 故障让本次写入抛错时
    /// <see cref="IsTerminal"/> 仍为 false，宿主的 <c>finally</c> 守卫得以重试终态写入（LC-STD-01 §11.2
    /// 「不会留下 running」）。重复 <c>cycle</c> SSE 由独立发布位防重：落库已成功、只是发布前中断的
    /// 重试路径不会为同一终态补发第二个 cycle 事件；终态行是原地更新，重试也不会重复插行。
    /// </summary>
    public async Task CompleteCycleAsync(TradingCycleContext ctx, string status, string? errorCode, string? error,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_cycle is null || _terminal) return;

        _cycle.Status = status;
        _cycle.ErrorCode = Truncate(errorCode, 64);
        _cycle.Error = Truncate(error, 1000);
        _cycle.CompletedAt = DateTime.UtcNow;
        _cycle.ElapsedMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _startedTimestamp);
        _cycle.InputTokens = _inputTokens;
        _cycle.OutputTokens = _outputTokens;
        await db.SaveChangesAsync(CancellationToken.None);
        _terminal = true;

        if (ctx.Trigger != "test") await TouchTraderAsync(ctx.Trader.Id, status);
        if (_terminalEventPublished) return;
        _terminalEventPublished = true;
        hub.Publish(_channelKey!, new SseEvent("cycle", CompletedPayload()));
    }

    private TradeProposalDto ProposalPayload(TradeProposal row) => new()
    {
        Id = row.Id,
        Action = row.Action,
        Symbol = row.Symbol,
        Side = row.Side,
        Confidence = Num(row.Confidence),
        EntryPrice = Num(row.EntryPrice),
        StopLossPrice = Num(row.StopLossPrice),
        TakeProfitPrice = Num(row.TakeProfitPrice),
        PositionPct = Num(row.PositionPct),
        Leverage = Num(row.Leverage),
        Thesis = row.Thesis,
        Invalidation = row.Invalidation,
    };

    private sealed record TranscriptEntry(string Role, string? Text, IReadOnlyList<string> Tools);

    private static AgentRole RoleOf(string role) => role switch
    {
        "analyst" => AgentRole.Analyst,
        "risk" => AgentRole.Risk,
        "executor" => AgentRole.Executor,
        "reviewer" => AgentRole.Reviewer,
        _ => AgentRole.Trader,
    };

    private static int SaturateAdd(int current, int? delta) => delta is not { } value
        ? current
        : value > 0 && current > int.MaxValue - value
            ? int.MaxValue
            : current + Math.Max(value, 0);

    private TradeProposal RecordProposal(TradingCycleContext ctx, AgentTradeProposal proposal)
    {
        var row = new TradeProposal
        {
            Id = Ids.NewUlid(),
            CycleId = ctx.CycleId,
            TraderId = ctx.Trigger == "test" ? null : ctx.Trader.Id,
            Action = proposal.Action.ToString().ToLowerInvariant(),
            Symbol = Truncate(proposal.Symbol, 50),
            Side = proposal.Side?.ToString().ToLowerInvariant(),
            Confidence = proposal.Confidence,
            EntryPrice = proposal.EntryPrice,
            StopLossPrice = proposal.StopLossPrice,
            TakeProfitPrice = proposal.TakeProfitPrice,
            PositionPct = proposal.PositionPct,
            Leverage = proposal.Leverage,
            Thesis = Truncate(proposal.Thesis, 2000) ?? "",
            Invalidation = Truncate(proposal.Invalidation, 1000),
            ReviewChecklistJson = proposal.ReviewChecklist is { Count: > 0 } checklist
                ? JsonSerializer.Serialize(checklist, ApiJson.Options)
                : null,
            RawJson = JsonSerializer.Serialize(proposal, AgentJson.Options),
            CreatedAt = DateTime.UtcNow,
        };
        db.TradeProposals.Add(row);
        _proposalId = row.Id;
        return row;
    }

    private VerdictRecordedEvent? RecordVerdict(TradingCycleContext ctx, VerdictMessage message)
    {
        // 一提案一裁决：没有已落库的提案 ID 就不写裁决（fail-closed，写工具随后也无法通过批准校验）。
        if (_proposalId is not { } proposalId) return null;

        var verdict = message.Verdict;
        var row = new RiskVerdict
        {
            Id = Ids.NewUlid(),
            CycleId = ctx.CycleId,
            ProposalId = proposalId,
            Decision = verdict.Decision.ToString().ToLowerInvariant(),
            ReasonsJson = JsonSerializer.Serialize(verdict.Reasons ?? [], ApiJson.Options),
            MaxPositionPct = verdict.MaxPositionPct,
            MaxLeverage = verdict.MaxLeverage,
            EntryZoneMin = verdict.EntryZoneMin,
            EntryZoneMax = verdict.EntryZoneMax,
            AllowMarket = verdict.AllowMarket,
            ValidUntilUtc = verdict.ValidUntilUtc,
            RawJson = JsonSerializer.Serialize(verdict, AgentJson.Options),
            CreatedAt = DateTime.UtcNow,
        };
        db.RiskVerdicts.Add(row);
        return new VerdictRecordedEvent(row.Id, row.CycleId, row.ProposalId, row.Decision,
            verdict.Reasons ?? [], Num(row.MaxPositionPct), Num(row.MaxLeverage), Num(row.EntryZoneMin),
            Num(row.EntryZoneMax), row.AllowMarket, row.ValidUntilUtc);
    }

    /// <summary>同一阶段行只写一次：重复落库会撞主键，也会让 stage 事件重复推送。</summary>
    private Task<bool> IsRecordedAsync(string stageRunId) =>
        db.AgentStageRuns.AsNoTracking().AnyAsync(run => run.Id == stageRunId, CancellationToken.None);

    private static string? Num(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 消息记录落库：整体 JSON 超过 128 KiB 时先丢弃末尾消息、只剩一条时按 UTF-8 字节预算裁文本，
    /// 保证落库内容始终是合法 JSON 且不超过上限，并置 <c>Truncated</c>。
    /// 对复盘宿主（Task 13 的 <see cref="ReviewRunner"/>）同样开放：截断口径只有一处实现，
    /// 避免阶段与复盘两套 128 KiB 规则漂移。
    /// </summary>
    internal static (string Json, bool Truncated) BuildTranscript(IReadOnlyList<ChatMessage> messages)
    {
        var entries = messages
            .Select(message => new TranscriptEntry(message.Role.Value, message.Text,
                message.Contents.OfType<FunctionCallContent>()
                    .Select(call => call.Name ?? "")
                    .ToList()))
            .ToList();

        var json = JsonSerializer.Serialize(entries, ApiJson.Options);
        if (Encoding.UTF8.GetByteCount(json) <= MaxTranscriptBytes) return (json, false);

        while (entries.Count > 1)
        {
            entries.RemoveAt(entries.Count - 1);
            json = JsonSerializer.Serialize(entries, ApiJson.Options);
            if (Encoding.UTF8.GetByteCount(json) <= MaxTranscriptBytes) return (json, true);
        }

        var only = entries[0];
        var overhead = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(new[] { only with { Text = "" } }, ApiJson.Options));
        var budget = Math.Max(0, MaxTranscriptBytes - overhead - Encoding.UTF8.GetByteCount(TruncationMarker));
        var trimmed = only with { Text = TrimToUtf8Bytes(only.Text, budget) + TruncationMarker };
        json = JsonSerializer.Serialize(new[] { trimmed }, ApiJson.Options);
        return Encoding.UTF8.GetByteCount(json) <= MaxTranscriptBytes
            ? (json, true)
            : (OmittedTranscriptJson, true);
    }

    private static string TrimToUtf8Bytes(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || maxBytes <= 0) return "";
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        // 按 UTF-8 字节数二分出最长的合规前缀（以 UTF-16 长度表达），再退掉半个代理对。
        var low = 0;
        var high = text!.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, middle)) <= maxBytes) low = middle;
            else high = middle - 1;
        }
        var cut = low;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }

    private static string? SummaryOf(object output) => Truncate(output switch
    {
        ResearchBrief brief => $"候选 {brief.Candidates.Count} 个；{brief.MarketContext}",
        ProposalMessage proposal => $"{proposal.Proposal.Action.ToString().ToLowerInvariant()} " +
            $"{proposal.Proposal.Symbol}：{proposal.Proposal.Thesis}",
        VerdictMessage verdict => $"{verdict.Verdict.Decision.ToString().ToLowerInvariant()}：" +
            string.Join("；", verdict.Verdict.Reasons),
        ExecutionReport report => report.Summary,
        _ => null,
    }, 500);

    private static string? Truncate(string? value, int maxChars) =>
        value is null || value.Length <= maxChars ? value : value[..maxChars];

    /// <summary>写回交易员最近一次周期状态（state 事件与看板的数据源）；不覆盖其他列。</summary>
    private Task TouchTraderAsync(int traderId, string status) =>
        db.Traders.Where(t => t.Id == traderId).ExecuteUpdateAsync(
            setters => setters
                .SetProperty(t => t.LastCycleAt, DateTime.UtcNow)
                .SetProperty(t => t.LastCycleStatus, status),
            CancellationToken.None);

    private CycleStartedEvent StartedPayload() => new(_cycle!.Id, _cycle.TraderId, _cycle.Mode, _cycle.Trigger,
        _cycle.Status, _cycle.StartedAt, _cycle.CompletedAt, _cycle.Error);

    private CycleCompletedEvent CompletedPayload() => new(_cycle!.Id, _cycle.TraderId, _cycle.Mode, _cycle.Trigger,
        _cycle.Status, _cycle.StartedAt, _cycle.CompletedAt, _cycle.Error);
}
