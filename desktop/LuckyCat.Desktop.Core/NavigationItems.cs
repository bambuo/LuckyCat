namespace LuckyCat.Desktop.Core;

public sealed record NavigationItem(string Path, string Title);

public static class NavigationItems
{
    public static IReadOnlyList<NavigationItem> All { get; } =
    [
        new("/traders", "AI 交易员"),
        new("/strategy", "策略工作室"),
        new("/cycles", "周期历史"),
        new("/reviews", "复盘经验"),
    ];

    public static NavigationItem? Match(string? urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return null;

        // Unix absolute paths parse as file: URIs, where AbsolutePath escapes '?' and '#'.
        var path = Uri.TryCreate(urlOrPath, UriKind.Absolute, out var absolute) && !absolute.IsFile
            ? absolute.AbsolutePath
            : urlOrPath;

        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];

        return All.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
    }
}
