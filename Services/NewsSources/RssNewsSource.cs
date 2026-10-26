using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LuckyCat.Services.NewsSources;

/// <summary>
/// 通用 RSS 2.0 / Atom 资讯源（Task 14.7）：一个适配器 + N 个注册（PANews、Cointelegraph、CoinDesk、
/// The Block、Decrypt、美联储新闻稿），无鉴权，固定浏览器 User-Agent。
/// 口径：
/// - 重定向交给 HttpClientHandler 默认行为（PANews 301 → /rss.xml、CoinDesk 308 → 无尾斜杠）；
/// - 解析：RSS 2.0 <c>item</c> 与 Atom <c>entry</c> 都支持，CDATA 由 XDocument 透明展开；
/// - 时间：优先 dc:date（部分源用它覆盖 pubDate），其次 pubDate、published、updated；
///   RFC 822（GMT / +0000）与 ISO-8601 都以 InvariantCulture +
///   AssumeUniversal|AdjustToUniversal 解析，渲染为 yyyy-MM-ddTHH:mm:ssZ；不可解析的条目跳过；
/// - 正文：description（剥离 HTML）优先，为空回落到 summary；标题同样剥离标签/实体；
///   标题与正文都为空则跳过该条；
/// - 每轮最多 <see cref="MaxItems"/> 条（取最新）、每条 Title/Text ≤200 字符（省略号计入）；
/// - 请求 10s 超时（linked CTS 覆盖响应体读取）、响应体 1MB 上限、BOM 容忍；
///   PANews 实测约 933KB，1MB 上限只剩约 90KB 余量：该源再显著变长会整源缺席（Problem 可见），
///   而不是静默截断出半篇 XML；
/// - 网络/解析失败只返回固定短中文 Problem，不抛异常；调用方取消原样传播；
/// - 单实例 TTL 缓存 600s；失败不缓存（与链捕手修复同口径）；调用方要得更少时裁剪缓存切片。
/// IHttpClientFactory 由 DI 以单例提供，本类只按请求创建 HttpClient，不持有长生命周期的 HttpClient。
/// </summary>
public sealed class RssNewsSource(string name, string url, IHttpClientFactory httpClientFactory) : INewsSource
{
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const int MaxItems = 50;
    private const int MaxItemChars = 200;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(600);
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly object _cacheGate = new();
    private DateTime _cachedAtUtc;
    private ExternalDataResult? _cached;

    public string Name => name;

    public async Task<ExternalDataResult> FetchAsync(int maxItems, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var limit = Math.Clamp(maxItems, 1, MaxItems);
        if (TryGetCached(limit, out var cached)) return cached;

        ExternalDataResult result;
        try
        {
            result = await FetchCoreAsync(limit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消：原样上抛，不折算成 Problem
            throw;
        }
        catch (OperationCanceledException)
        {
            result = Failure("拉取超时");
        }
        catch (Exception ex)
        {
            // 异常原文可能含上游 URL/凭据形状内容，Problem 只给固定中文原因（LC-STD-02）；
            // 异常类型名只进日志，便于区分上游故障与自身缺陷。
            Console.WriteLine($"[News] {Name} 拉取异常（{ex.GetType().Name}）");
            result = Failure("拉取失败");
        }

        if (result.Problem is not null)
        {
            Console.WriteLine($"[News] {Name} 本轮缺席（{LogRedaction.Scrub(result.Problem)}）");
        }

        result = result with { FetchedAtUtc = DateTime.UtcNow };
        // 失败不缓存：600s 的失败窗口会让一次瞬时抖动把该源藏 10 分钟（外部源无 SLA，宁可下轮重试）
        if (result.Problem is null) Store(result);
        return result;
    }

    private async Task<ExternalDataResult> FetchCoreAsync(int maxItems, CancellationToken ct)
    {
        // 显式 10s 预算覆盖响应体读取（ResponseHeadersRead 下 HttpClient.Timeout 只保证到响应头）
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        var token = timeoutCts.Token;

        var http = httpClientFactory.CreateClient();
        http.Timeout = RequestTimeout;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Failure($"服务返回 {(int)response.StatusCode}");
        }

        var body = await ReadLimitedAsync(response, token).ConfigureAwait(false);
        return Parse(body, maxItems);
    }

    // 1MB 上限（与 ExternalDataSourceService 一致），避免异常大响应拖垮周期
    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        while (total < MaxBodyBytes)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read <= 0) break;
            var take = Math.Min(read, MaxBodyBytes - total);
            buffer.Write(chunk, 0, take);
            total += take;
        }
        // 美联储新闻稿等源在 XML 声明前带 UTF-8 BOM；先剥掉，避免 XDocument 把 BOM 当作内容
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    private ExternalDataResult Parse(string body, int maxItems)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body, LoadOptions.None);
        }
        catch (XmlException)
        {
            return Failure("XML 解析失败");
        }

        var parsed = new List<(DateTimeOffset At, ExternalDataItem Item)>();
        foreach (var entry in document.Descendants().Where(element => element.Name.LocalName is "item" or "entry"))
        {
            var item = ParseEntry(entry);
            if (item is not null) parsed.Add(item.Value);
        }
        if (parsed.Count == 0) return Failure("暂无资讯");

        var selected = parsed
            .OrderByDescending(entry => entry.At)
            .Take(maxItems)
            .Select(entry => entry.Item)
            .ToList();
        return new ExternalDataResult(Name, selected, null);
    }

    private static (DateTimeOffset At, ExternalDataItem Item)? ParseEntry(XElement entry)
    {
        var title = StripHtml(ChildText(entry, "title"));
        // RSS 2.0 用 description；Atom 用 summary（description 为空时才回落）
        var text = StripHtml(ChildText(entry, "description"));
        if (text.Length == 0) text = StripHtml(ChildText(entry, "summary"));
        if (title.Length == 0 && text.Length == 0) return null;

        // 时间不可解析（含缺 pubDate）时跳过该条：不产出无时间戳条目，跨源去重与排序才有意义
        var at = ParseEntryTime(entry);
        if (at is null) return null;

        return (at.Value, new ExternalDataItem(FormatUtc(at.Value), Clamp(title), Clamp(text)));
    }

    private static DateTimeOffset? ParseEntryTime(XElement entry)
    {
        // dc:date 优先（链捕手对接文档建议：部分源用 dc:date 覆盖 pubDate）；其后兼容 Atom 的 published/updated
        string?[] candidates =
        [
            ChildText(entry, Dc + "date"),
            ChildText(entry, "pubDate"),
            ChildText(entry, "published"),
            ChildText(entry, "updated"),
        ];
        foreach (var raw in candidates)
        {
            if (ParseUtc(raw) is { } at) return at;
        }
        return null;
    }

    // RFC 822 / ISO-8601 统一口径：无时区标记按 UTC 解释，再归一为 UTC（绝不落机器本地时区）
    private static DateTimeOffset? ParseUtc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    private static string? ChildText(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value.Trim();

    private static string? ChildText(XElement element, XName childName) =>
        element.Element(childName)?.Value.Trim();

    private static string FormatUtc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // 小体量本地清洗（既有 StripHtml 为 ExternalDataSourceService 私有；不复制其整段实现）
    private static string StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var withoutTags = HtmlTagRegex.Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    // 严格 ≤200（省略号计入预算），与外部源「每条 ≤200 字符」口径一致
    private static string Clamp(string value) =>
        value.Length <= MaxItemChars ? value : value[..(MaxItemChars - 1)] + "…";

    private ExternalDataResult Failure(string reason) => new(Name, [], reason);

    private bool TryGetCached(int maxItems, out ExternalDataResult result)
    {
        lock (_cacheGate)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
            {
                // 缓存按最大条数存放：调用方要得更少时按需裁剪，不越过 INewsSource 的 maxItems 契约
                result = _cached.Items.Count <= maxItems
                    ? _cached
                    : _cached with { Items = _cached.Items.Take(maxItems).ToList() };
                return true;
            }
        }
        result = null!;
        return false;
    }

    private void Store(ExternalDataResult result)
    {
        lock (_cacheGate)
        {
            _cached = result;
            _cachedAtUtc = DateTime.UtcNow;
        }
    }
}
