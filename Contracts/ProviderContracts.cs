using LuckyCat.Models;

namespace LuckyCat.Contracts;

public record ProviderDto(
    int Id,
    string Name,
    ProviderType Type,
    string BaseUrl,
    string ApiKeyMasked,
    string Model,
    string? ApiVersion,
    string ThinkingMode,
    int ContextTokens,
    int MaxOutputTokens,
    bool AutoCompressContext,
    /// <summary>工具调用验证结果（null=未验证）；start/test-run 前置校验依赖本字段（LC-STD-03 §2.2）。</summary>
    bool? ToolCallingSupported,
    DateTime? ToolCallingVerifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ProviderUpsertRequest(
    string Name,
    ProviderType Type,
    string BaseUrl,
    string? ApiKey,
    string Model,
    string? ApiVersion,
    string? ThinkingMode = null,
    int? ContextTokens = null,
    int? MaxOutputTokens = null,
    bool? AutoCompressContext = null);

public static class ApiKeyMasker
{
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length < 8) return "…";
        return $"{key[..3]}…{key[^4..]}";
    }
}
