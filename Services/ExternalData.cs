namespace LuckyCat.Services;

/// <summary>新闻源的单条内容。</summary>
public record ExternalDataItem(string Time, string Title, string Text);

/// <summary>新闻源的一轮拉取结果；Problem 非空表示该源本轮缺席（网络失败 / 无有效条目 / 解析失败）。</summary>
public record ExternalDataResult(string Name, IReadOnlyList<ExternalDataItem> Items, string? Problem, DateTime? FetchedAtUtc = null);
