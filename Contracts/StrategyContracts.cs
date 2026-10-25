using LuckyCat.Agents;

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

public record StrategyDefaultsDto(
    StrategyConfig Config,
    PromptSectionConfig PromptSections);

public record PromptPreviewRequest(StrategyConfig? Config);

/// <summary>preview-prompts 单角色产物：系统指令全文与估算 token（LC-STD-03 §3.3，不调用模型）。</summary>
public record RolePromptPreviewDto(AgentRole Role, string Prompt, int EstimatedTokens);

public record PromptPreviewResultDto(IReadOnlyList<RolePromptPreviewDto> Roles);

/// <summary>validate 通过时的载荷；不通过走 400 validation_failed（错误码表 LC-STD-03 §6）。</summary>
public record StrategyValidationDto(bool Valid, IReadOnlyList<string> Errors);

/// <summary>
/// 新版 test-run 请求（LC-STD-03 §3.3）：SSE 演练完整工作流。
/// testEquity 省略时默认 "10000"（字符串金额，§1.1）；includeMemory 默认 true；
/// accountId 为契约预留字段——B 组实现只支持合成账户演练，非 null 一律 400 拒绝（见 task-14 报告）。
/// </summary>
public record StrategyRunTestRequest(
    int StrategyId,
    int ProviderId,
    int? AccountId = null,
    string? TestEquity = null,
    bool IncludeMemory = true);
