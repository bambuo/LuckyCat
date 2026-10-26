using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LuckyCat.Services.NewsSources;

/// <summary>
/// 金十数据快讯内置源（Task 14.5，唯一内置来源；链捕手 / RootData 经实测否决，不实现）：
/// GET https://flash-api.jin10.com/get_flash_list?channel=-8200&amp;vip=1，无 API Key，固定客户端头。
/// 口径：
/// - 上游 <c>data[].time</c> 是北京时间（UTC+8、无时区标记），按 UTC+8 解析后渲染为 ISO-8601 UTC（Z 结尾），
///   绝不使用机器本地时区（本机 UTC+7 会把每条快讯静默平移一小时）；
/// - 正文/标题剥离 HTML 与实体，<c>important</c>（线上为 0/1 数字，兼容布尔）加「【重要】」前缀；
/// - 每轮最多 10 条（取最新）、每条 Text ≤200 字符；请求 10s 超时、响应体 1MB 上限；
/// - 网络/解析失败只返回 Problem（短中文原因），调用方取消原样传播；
/// - 单实例 TTL 缓存 300s（只保留一条结果，成功与失败都在 TTL 内复用）。
/// IHttpClientFactory 由 DI 以单例提供，本类只按请求创建 HttpClient，不持有长生命周期的 HttpClient。
/// </summary>
public sealed class Jin10NewsSource(IHttpClientFactory httpClientFactory) : INewsSource
{
    public const string SourceName = "金十数据";
    private const string Url = "https://flash-api.jin10.com/get_flash_list?channel=-8200&vip=1";
    private const string AppId = "bVBF4FyRTn5NJF5n";
    private const string AppVersion = "1.0.0";
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const int MaxItems = 10;
    private const int MaxItemChars = 200;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
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
        catch (Exception)
        {
            // 异常原文可能含上游 URL/凭据形状内容，Problem 只给固定中文原因（LC-STD-02）
            result = Failure("拉取失败");
        }

        if (result.Problem is not null)
        {
            Console.WriteLine($"[News] {Name} 本轮缺席（{LogRedaction.Scrub(result.Problem)}）");
        }

        result = result with { FetchedAtUtc = DateTime.UtcNow };
        Store(result);
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
        request.Headers.TryAddWithoutValidation("x-app-id", AppId);
        request.Headers.TryAddWithoutValidation("x-version", AppVersion);
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

    // 1MB 上限，避免异常大响应拖垮周期
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
        // 个别网关会在 JSON 前带 BOM；JsonDocument 不接受，先剥掉
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
            if (root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.Number
                && status.TryGetInt32(out var code)
                && code != 200)
            {
                return Failure($"服务返回 {code}");
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Failure("返回格式异常");
            }

            var parsed = new List<(DateTimeOffset? At, ExternalDataItem Item)>();
            foreach (var element in data.EnumerateArray())
            {
                var item = ParseItem(element);
                if (item is not null) parsed.Add(item.Value);
            }
            if (parsed.Count == 0) return Failure("暂无快讯");

            var items = parsed
                .OrderByDescending(entry => entry.At ?? DateTimeOffset.MinValue)
                .Take(maxItems)
                .Select(entry => entry.Item)
                .ToList();
            return new ExternalDataResult(Name, items, null);
        }
    }

    private static (DateTimeOffset? At, ExternalDataItem Item)? ParseItem(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;

        var title = StripHtml(ReadString(payload, "title"));
        var text = StripHtml(ReadString(payload, "content"));
        // 正文为空时退回标题（部分快讯只有 title）；两者都空才是无可用文本的占位条目
        if (text.Length == 0) text = title;
        if (text.Length == 0) return null;

        // 时间格式漂移时宁可整源缺席（Problem 可见），也不产出无时间戳的条目——
        // 无时间会让跨源去重退化为「文本|空串」，且排序把新快讯沉底。
        var at = ParseBeijingTime(ReadString(element, "time"));
        if (at is null) return null;

        if (IsImportant(element)) text = "【重要】" + text;
        return (at, new ExternalDataItem(FormatUtc(at), Clamp(title), Clamp(text)));
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // 线上 important 为 0/1 数字（用户实测记录为布尔）；两种形状都接受，字符串 "1"/"true" 一并容忍
    private static bool IsImportant(JsonElement element)
    {
        if (!element.TryGetProperty("important", out var marker)) return false;
        return marker.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => marker.TryGetDouble(out var number) && number != 0,
            JsonValueKind.String => marker.GetString()?.Trim().ToLowerInvariant() is "1" or "true",
            _ => false,
        };
    }

    // 上游时间无时区标记：一律按 UTC+8 解释，绝不落到机器本地时区（UTC+7 会静默平移一小时）
    private static DateTimeOffset? ParseBeijingTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTime.TryParseExact(raw.Trim(), TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var beijing))
        {
            return null;
        }
        return new DateTimeOffset(DateTime.SpecifyKind(beijing, DateTimeKind.Unspecified), BeijingOffset);
    }

    private static string FormatUtc(DateTimeOffset? at) =>
        at?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "";

    // 小体量本地清洗（新闻源只需去标签，不引入完整 HTML 解析）
    private static string StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var withoutTags = HtmlTagRegex.Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    // 严格 ≤200（省略号计入预算），与新闻源「每条 ≤200 字符」口径一致
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
