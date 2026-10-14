using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace LuckyCat.Services;

/// <summary>提示词中的 JSON 保留可读中文，仍转义引号、换行和 HTML 特殊字符。</summary>
public static class PromptJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
