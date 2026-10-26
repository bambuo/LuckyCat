using System.Text;
using System.Text.Json;
using LuckyCat.Contracts;

namespace LuckyCat.Tools;

/// <summary>工具错误：Code 为稳定错误码（<see cref="ToolErrors"/>），Retryable 表示同一周期内可重试。</summary>
public sealed record ToolError(string Code, string Message, bool Retryable);

/// <summary>
/// 统一工具回执：成功携带紧凑数据（字符串或 JSON）、数据时点与来源；失败携带错误码。
/// <c>Source</c> 只表达数据完整性（<c>live</c> / <c>incomplete</c>）；来源方名称放在 data 或 warnings。
/// 序列化后超过 16 KiB 时按行裁剪（或整体替换为 "<c>[truncated]</c>" 标记）并置 <c>truncated=true</c>，
/// 保证回执始终是合法 JSON 且 UTF-8 字节数不超过 16 KiB。
/// </summary>
public sealed record ToolEnvelope(bool Ok, JsonElement? Data, DateTimeOffset? AsOfUtc,
    string Source, IReadOnlyList<string> Warnings, bool Truncated, ToolError? Error)
{
    public const int MaxSerializedBytes = 16 * 1024;
    private const string TruncatedWarning = "结果超过 16 KiB，已截断";
    private const string OmittedWarning = "结果超过 16 KiB，已省略 {0} 行";

    public static ToolEnvelope Success(JsonElement data, DateTimeOffset? asOf, string source,
        IReadOnlyList<string>? warnings = null)
    {
        var envelope = new ToolEnvelope(true, data, asOf, NormalizeSource(source), warnings ?? [], false, null);
        return EnforceSize(envelope);
    }

    public static ToolEnvelope Success(string text, DateTimeOffset? asOf, string source,
        IReadOnlyList<string>? warnings = null) =>
        Success(JsonSerializer.SerializeToElement(text), asOf, source, warnings);

    /// <summary>
    /// 逐行构造成功回执：超过 16 KiB 时按行裁剪（<paramref name="trimOldest"/> 从头部删除最旧行，
    /// 适合按时间排序的 K 线/新闻），保留 header/footer 包裹，置 truncated=true 且 source=incomplete。
    /// </summary>
    public static ToolEnvelope SuccessLines(IReadOnlyList<string> lines, DateTimeOffset? asOf, string source,
        IReadOnlyList<string>? warnings = null, bool trimOldest = false, string? header = null, string? footer = null)
    {
        var kept = lines.ToList();
        var removed = 0;
        while (true)
        {
            var warningsNow = warnings?.ToList() ?? [];
            if (removed > 0) warningsNow.Add(string.Format(OmittedWarning, removed));
            var envelope = new ToolEnvelope(true, JsonSerializer.SerializeToElement(Compose(kept, header, footer)),
                asOf, removed > 0 ? "incomplete" : NormalizeSource(source), warningsNow, removed > 0, null);
            if (SerializedBytes(envelope) <= MaxSerializedBytes) return EnforceSize(envelope);
            if (kept.Count == 0) return EnforceSize(envelope);
            kept.RemoveAt(trimOldest ? 0 : kept.Count - 1);
            removed++;
        }
    }

    public static ToolEnvelope Failure(string code, string message, bool retryable = false) =>
        new(false, null, null, "incomplete", [], false, new ToolError(code, message, retryable));

    /// <summary>回执按 API 序列化后的 UTF-8 字节数（16 KiB 上限的计量口径）。</summary>
    public static int SerializedBytes(ToolEnvelope envelope) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(envelope, ApiJson.Options));

    private static string NormalizeSource(string source) => source == "live" ? "live" : "incomplete";

    private static string Compose(IReadOnlyList<string> lines, string? header, string? footer)
    {
        var parts = new List<string>(lines.Count + 2);
        if (header is not null) parts.Add(header);
        parts.AddRange(lines);
        if (footer is not null) parts.Add(footer);
        return string.Join('\n', parts);
    }

    private static ToolEnvelope EnforceSize(ToolEnvelope envelope)
    {
        if (SerializedBytes(envelope) <= MaxSerializedBytes) return envelope;
        var truncated = envelope with
        {
            Data = JsonSerializer.SerializeToElement("[truncated]"),
            Source = "incomplete",
            Truncated = true,
            Warnings = envelope.Warnings.Append(TruncatedWarning).ToArray()
        };
        while (SerializedBytes(truncated) > MaxSerializedBytes && truncated.Warnings.Count > 1)
        {
            truncated = truncated with { Warnings = truncated.Warnings.Take(truncated.Warnings.Count - 1).ToArray() };
        }
        return truncated;
    }
}
