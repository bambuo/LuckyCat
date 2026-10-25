using System.Text.Json;

namespace LuckyCat.Services;

/// <summary>
/// 旧单发演练执行器（行情拼装、提示词、上下文压缩、执行副作用）已在 P7 整体删除。
/// 仅保留两处仍有活引用的纯函数：
/// 1. <see cref="EstimateTokens"/>——preview-prompts 的粗估口径（CJK≈1 token/字）；
/// 2. <see cref="TryParseDecision"/>——旧 JSON 决策契约的解析/校验，TradingRiskPolicyTests 以它
///    钉住「非法契约不得产生可执行计划」（R-18-4：该测试文件不得改动）。
/// 新链路的模型输出解析走 <see cref="LuckyCat.Agents.AgentRunner"/>（AgentJson 契约），不经过本类。
/// </summary>
public static class StrategyTestRunner
{
    /// <summary>粗估 token（CJK≈1 token/字，其余≈4 字符/token）；预览展示用，不用于计费。</summary>
    internal static int EstimateTokens(string text)
    {
        var cjk = 0;
        var other = 0;
        foreach (var c in text)
        {
            if (c is >= '\u4e00' and <= '\u9fff') cjk++;
            else other++;
        }
        return cjk + other / 4;
    }

    // 依次尝试：原文 / 首个 { 到末个 } 的子串；解析出 decision（含 reasoning 或直接从 action 起）
    internal static JsonElement? TryParseDecision(string raw, out string? reasoning) => ParseDecision(raw, out reasoning, out _);

    internal static JsonElement? ParseDecision(string raw, out string? reasoning, out string? error)
    {
        error = "模型响应为空";
        reasoning = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        error = "模型响应不是合法 JSON 决策对象";
        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        var candidates = start >= 0 && end > start
            ? new[] { trimmed, trimmed[start..(end + 1)] }
            : new[] { trimmed };

        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                if (doc.RootElement.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                {
                    reasoning = r.GetString();
                }
                if (doc.RootElement.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    error = TradingRiskPolicy.ValidateDecision(d);
                    return error is null ? d.Clone() : null;
                }
                // 模型直接输出 decision 内容（无外层包装）也接受
                if (doc.RootElement.TryGetProperty("action", out _))
                {
                    error = TradingRiskPolicy.ValidateDecision(doc.RootElement);
                    return error is null ? doc.RootElement.Clone() : null;
                }
            }
            catch (JsonException)
            {
                // 尝试下一个候选
            }
        }
        return null;
    }
}
