using System.Text.Json;
using LuckyCat.Contracts;

namespace LuckyCat.Tools;

/// <summary>工具错误：Code 为稳定错误码（<see cref="ToolErrors"/>），Retryable 表示同一周期内可重试。</summary>
public sealed record ToolError(string Code, string Message, bool Retryable);

/// <summary>
/// 统一工具回执：成功携带紧凑数据（字符串或 JSON）、数据时点与来源；失败携带错误码。
/// 序列化后超过 16 KiB 时整体截断（Data 替换为 "[truncated]"），避免单次工具结果撑爆模型上下文。
/// </summary>
public sealed record ToolEnvelope(bool Ok, JsonElement? Data, DateTimeOffset? AsOfUtc,
    string Source, IReadOnlyList<string> Warnings, bool Truncated, ToolError? Error)
{
    public static ToolEnvelope Success(JsonElement data, DateTimeOffset? asOf, string source,
        IReadOnlyList<string>? warnings = null)
    {
        var envelope = new ToolEnvelope(true, data, asOf, source, warnings ?? [], false, null);
        return EnforceSize(envelope);
    }

    public static ToolEnvelope Success(string text, DateTimeOffset? asOf, string source,
        IReadOnlyList<string>? warnings = null) =>
        Success(JsonSerializer.SerializeToElement(text), asOf, source, warnings);

    public static ToolEnvelope Failure(string code, string message, bool retryable = false) =>
        new(false, null, null, "incomplete", [], false, new ToolError(code, message, retryable));

    private static ToolEnvelope EnforceSize(ToolEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope, ApiJson.Options);
        if (json.Length <= 16 * 1024) return envelope;
        var truncated = envelope with
        {
            Data = JsonSerializer.SerializeToElement("[truncated]"),
            Truncated = true,
            Warnings = envelope.Warnings.Append("结果超过 16 KiB，已截断").ToArray()
        };
        return truncated;
    }
}
