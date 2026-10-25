using System.Text.RegularExpressions;

namespace LuckyCat.Services;

/// <summary>
/// 服务端日志脱敏（LC-STD-02 §10、LC-STD-04 §8）：上游异常原文可能带含凭据的 URL 或
/// key=/token=/Authorization 形状的凭据片段，进日志前必须先截断再遮蔽——宁可多遮，不可泄露。
/// 只作用于日志行；落库与 SSE 负载仍然是稳定错误码 + 固定中文摘要（R-13-7），与本函数无关。
/// </summary>
public static class LogRedaction
{
    /// <summary>日志行上限：上游超长回传不得整段进日志。</summary>
    public const int MaxLoggedChars = 4_000;

    /// <summary>截断 + URL 整体替换 + 凭据形状片段起至行尾替换（异常消息逐行处理，堆栈行原样保留）。</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clipped = text.Length <= MaxLoggedChars ? text : text[..MaxLoggedChars];
        var scrubbed = Url.Replace(clipped, "[url]");
        scrubbed = Credential.Replace(scrubbed, "$1=[redacted]");
        return scrubbed;
    }

    private static readonly Regex Url = new(@"https?://\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 凭据形状：authorization / api key / token / key 等片段之后的整行内容一律遮蔽（宁可多遮）。
    private static readonly Regex Credential = new(
        @"(?i)\b(authorization|api[-_ ]?key|access[-_ ]?key|secret[-_ ]?key|secret|passphrase|private[-_ ]?key|token|key)\b[^\r\n]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
