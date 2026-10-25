using System.Text.Json;

namespace LuckyCat.Contracts;

public record StrategyDto(
    int Id,
    string Name,
    string? Description,
    bool IsActive,
    StrategyConfig Config,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record StrategyUpsertRequest(
    string Name,
    string? Description,
    StrategyConfig? Config);

public record PromptSummaryItem(string Label, string Value);

/// <summary>币种来源的真实解析结果（提示词展示用；空/未启用为 null）。</summary>
public record CoinSourceTexts(
    IReadOnlyList<string>? Score = null,
    IReadOnlyList<string>? OiTop = null,
    IReadOnlyList<string>? OiLow = null,
    IReadOnlyList<string>? Merged = null);

public record StrategyDefaultsDto(
    StrategyConfig Config,
    PromptSectionConfig PromptSections);

public record PromptPreviewRequest(StrategyConfig? Config);

public record PromptPreviewDto(
    string SystemPrompt,
    List<PromptSummaryItem> Summary);

/// <summary>preview-prompts 单角色产物：系统指令全文与估算 token（LC-STD-03 §3.3，不调用模型）。</summary>
public record RolePromptPreviewDto(LuckyCat.Agents.AgentRole Role, string Prompt, int EstimatedTokens);

public record PromptPreviewResultDto(IReadOnlyList<RolePromptPreviewDto> Roles);

/// <summary>validate 通过时的载荷；不通过走 400 validation_failed（错误码表 LC-STD-03 §6）。</summary>
public record StrategyValidationDto(bool Valid, IReadOnlyList<string> Errors);

public record StrategyTestRequest(
    StrategyConfig? Config,
    int ProviderId,
    int? StrategyId = null);

public record StrategyTestResultDto(
    long ElapsedMs,
    string UserPrompt,
    string RawOutput,
    string? Reasoning,
    JsonElement? Decision,
    string DataSource,
    int EstimatedTokens,
    string? ContextWarning,
    string? Error,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? SystemPrompt = null,
    IReadOnlyList<string>? ExecutableSymbols = null,
    DateTime? MarketAsOf = null,
    IReadOnlyDictionary<string, DateTime>? SymbolMarketAsOf = null,
    string? FailureStage = null,
    bool RequestSent = false,
    IReadOnlyDictionary<string, SignalAssessment>? SignalAssessments = null);
