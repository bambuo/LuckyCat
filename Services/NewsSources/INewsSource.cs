namespace LuckyCat.Services.NewsSources;

/// <summary>
/// 内置新闻源适配器（Task 14.5）：把特定厂商的快讯接口归一为既有的
/// <see cref="ExternalDataResult"/> / <see cref="ExternalDataItem"/>，使工具与提示词层无需感知来源差异。
/// 适配器只允许在失败时返回 Problem，不得把网络/解析异常抛给调用方；调用方取消必须原样传播。
/// </summary>
public interface INewsSource
{
    /// <summary>展示用来源名称（进入 get_news 的行首与 warnings）。</summary>
    string Name { get; }

    /// <summary>拉取最近 <paramref name="maxItems"/> 条（按时间倒序，时间已归一为 ISO-8601 UTC）。</summary>
    Task<ExternalDataResult> FetchAsync(int maxItems, CancellationToken ct);
}
