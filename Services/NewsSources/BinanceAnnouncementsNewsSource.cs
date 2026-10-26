using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LuckyCat.Services.NewsSources;

/// <summary>
/// 币安公告内置源（Task 14.7）：
/// GET https://www.binance.com/bapi/apex/v1/public/apex/cms/article/list/query?type=1&amp;pageNo=1&amp;pageSize=20，
/// 无鉴权，固定浏览器 User-Agent。
/// 口径：
/// - 响应信封：成功判断只看业务字段 <c>code == "000000"</c>（不依赖 HTTP 状态码）；
///   <c>message</c> 是英文技术文案，只经 LogRedaction.Scrub 记日志，不进 Problem、不透传用户；
/// - <c>data.catalogs[].articles[]</c> 全量展开；catalog 允许嵌套 <c>catalogs</c>，递归到
///   <see cref="MaxCatalogDepth"/> 层（线上目前为 7 个顶层 catalog、嵌套数组为空）；
/// - 时间：<c>releaseDate</c> 为 Unix 毫秒，渲染为 ISO-8601 UTC（Z 结尾）；字符串/缺失/越界时间的条目跳过；
/// - 公告无正文：Title = 公告标题（剥离标签/实体），Text 回落到同一标题（与金十「仅标题」条目同口径）；
/// - 每轮最多 <see cref="MaxItems"/> 条（取最新）、每条 Title/Text ≤200 字符；
///   请求 10s 超时（linked CTS 覆盖响应体读取）、响应体 1MB 上限、BOM 容忍；
/// - 网络/解析失败只返回固定短中文 Problem，不抛异常；调用方取消原样传播；
/// - 单实例 TTL 缓存 600s；失败不缓存（与链捕手修复同口径）；调用方要得更少时裁剪缓存切片。
/// IHttpClientFactory 由 DI 以单例提供，本类只按请求创建 HttpClient，不持有长生命周期的 HttpClient。
/// </summary>
public sealed class BinanceAnnouncementsNewsSource(IHttpClientFactory httpClientFactory) : INewsSource
{
    public const string SourceName = "Binance 公告";
    private const string Url =
        "https://www.binance.com/bapi/apex/v1/public/apex/cms/article/list/query?type=1&pageNo=1&pageSize=20";
    private const string SuccessCode = "000000";
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const int MaxItems = 50;
    private const int MaxItemChars = 200;
    private const int MaxBodyBytes = 1024 * 1024;
    private const int MaxCatalogDepth = 8;
    // DateTimeOffset.FromUnixTimeMilliseconds 的可表示边界，越界会抛 ArgumentOutOfRangeException
    private const long MinUnixMilliseconds = -62_135_596_800_000;
    private const long MaxUnixMilliseconds = 253_402_300_799_999;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(600);
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly object _cacheGate = new();
    private DateTime _cachedAtUtc;
    private ExternalDataResult? _cached;

    public string Name => SourceName;

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

        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
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
        // 个别网关会在 JSON 前带 BOM；JsonDocument 不接受，先剥掉（与 ExternalDataSourceService 同口径）
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    private ExternalDataResult Parse(string body, int maxItems)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Failure("JSON 解析失败");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Failure("返回格式异常");
            var code = ReadString(root, "code");
            if (code != SuccessCode)
            {
                // HTTP 恒 200，失败只体现在 code + 英文 message；message 仅记日志，不透传用户
                Console.WriteLine(
                    $"[News] {Name} 上游返回 code={code}（{LogRedaction.Scrub(ReadString(root, "message"))}）");
                return Failure("服务返回错误");
            }
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("catalogs", out var catalogs)
                || catalogs.ValueKind != JsonValueKind.Array)
            {
                return Failure("返回格式异常");
            }

            var parsed = new List<(DateTimeOffset At, ExternalDataItem Item)>();
            CollectArticles(catalogs, parsed, 0);
            if (parsed.Count == 0) return Failure("暂无公告");

            var selected = parsed
                .OrderByDescending(entry => entry.At)
                .Take(maxItems)
                .Select(entry => entry.Item)
                .ToList();
            return new ExternalDataResult(Name, selected, null);
        }
    }

    // 递归展开 catalogs[].articles[]；嵌套 catalogs 也一并展开（线上顶层 7 个、嵌套为空）
    private static void CollectArticles(
        JsonElement catalogs, List<(DateTimeOffset At, ExternalDataItem Item)> parsed, int depth)
    {
        if (depth > MaxCatalogDepth) return;
        foreach (var catalog in catalogs.EnumerateArray())
        {
            if (catalog.ValueKind != JsonValueKind.Object) continue;
            if (catalog.TryGetProperty("articles", out var articles) && articles.ValueKind == JsonValueKind.Array)
            {
                foreach (var article in articles.EnumerateArray())
                {
                    var item = ParseArticle(article);
                    if (item is not null) parsed.Add(item.Value);
                }
            }
            if (catalog.TryGetProperty("catalogs", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                CollectArticles(nested, parsed, depth + 1);
            }
        }
    }

    private static (DateTimeOffset At, ExternalDataItem Item)? ParseArticle(JsonElement article)
    {
        if (article.ValueKind != JsonValueKind.Object) return null;

        var title = StripHtml(ReadString(article, "title"));
        if (title.Length == 0) return null;

        // 时间漂移/越界时跳过该条，不产出无时间戳条目
        if (!article.TryGetProperty("releaseDate", out var release)
            || release.ValueKind != JsonValueKind.Number
            || !release.TryGetInt64(out var milliseconds))
        {
            return null;
        }
        if (milliseconds < MinUnixMilliseconds || milliseconds > MaxUnixMilliseconds) return null;

        var at = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        // 公告接口无正文：Text 回落到标题（与金十「仅标题」条目同口径），不编造摘要
        return (at, new ExternalDataItem(FormatUtc(at), Clamp(title), Clamp(title)));
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
