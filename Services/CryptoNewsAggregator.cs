using LuckyCat.Services.NewsSources;

namespace LuckyCat.Services;

/// <summary>
/// 新闻聚合（Task 14.5/14.6/14.7）：11 个内置来源（中文快讯/交易所公告/宏观日历/英文媒体）并行拉取，
/// 合并为一个 <see cref="ExternalDataResult"/> 列表供 get_news 消费。
/// 去重键 = (标题；标题为空时取正文前 <see cref="DedupeTextPrefixChars"/> 字符, 时间)，跨源重复的快讯只保留先出现的一条。
/// 单个来源失败（Problem 或意外异常）只降级为带 Problem 的结果，不吞掉其他来源；调用方取消原样传播。
/// </summary>
public sealed class CryptoNewsAggregator(IEnumerable<INewsSource> builtInSources)
{
    private const int MaxItems = 10;
    private const int DedupeTextPrefixChars = 40;
    private static readonly System.Text.RegularExpressions.Regex WhitespaceRegex =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<IReadOnlyList<ExternalDataResult>> FetchAsync(CancellationToken ct)
    {
        var builtIn = await Task.WhenAll(builtInSources.Select(source => FetchBuiltInAsync(source, ct)).ToArray())
            .ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<ExternalDataResult>(builtIn.Length);
        foreach (var result in builtIn)
        {
            var kept = new List<ExternalDataItem>(result.Items.Count);
            foreach (var item in result.Items)
            {
                if (seen.Add(DedupeKey(item))) kept.Add(item);
            }
            merged.Add(kept.Count == result.Items.Count ? result : result with { Items = kept });
        }
        return merged;
    }

    private static async Task<ExternalDataResult> FetchBuiltInAsync(INewsSource source, CancellationToken ct)
    {
        try
        {
            return await source.FetchAsync(MaxItems, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 适配器自身应已收敛异常；这里兜底，保证一个坏源不拖垮整轮新闻
            return new ExternalDataResult(source.Name, [], "拉取失败");
        }
    }

    private static string DedupeKey(ExternalDataItem item)
    {
        var title = Normalize(item.Title);
        var basis = title.Length > 0 ? title : TextPrefix(Normalize(item.Text));
        return $"{basis}|{Normalize(item.Time)}";
    }

    private static string TextPrefix(string text) =>
        text.Length <= DedupeTextPrefixChars ? text : text[..DedupeTextPrefixChars];

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : WhitespaceRegex.Replace(value, " ").Trim().ToLowerInvariant();
}
