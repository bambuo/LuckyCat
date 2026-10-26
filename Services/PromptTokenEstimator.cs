namespace LuckyCat.Services;

/// <summary>
/// prompt 预览的 token 粗估口径（原 <c>StrategyTestRunner</c> 旧执行器在 P7 删除后保留的纯函数）。
/// 仅供 preview-prompts 展示，不用于计费。
/// </summary>
public static class PromptTokenEstimator
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
}
