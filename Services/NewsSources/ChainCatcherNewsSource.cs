using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LuckyCat.Services.NewsSources;

/// <summary>
/// 链捕手快讯内置源（Task 14.6，仅 type=flash，不拉文章、不走 RSS）：
/// GET https://api.chaincatcher.com/v1/open-api/news-flash?type=flash&amp;lang=zh-CN&amp;size=N，无鉴权，
/// 固定浏览器 User-Agent（部分边缘节点拦截非浏览器 UA）。
/// 口径：
/// - 响应信封：上游 HTTP 状态恒为 200，成功与否只看业务字段 result（1 成功 / 0 失败）；
///   result=0 的英文 message 只经 LogRedaction.Scrub 记日志，不进 Problem、不透传用户；
///   data.total 是「自当前页起的剩余条数」，不作为终止/总量/条数上限信号；
/// - 时间：releaseTimeStamp 为 Unix 毫秒，渲染为 ISO-8601 UTC（Z 结尾）；不可解析/越界的条目直接跳过，
///   全部跳过则整源 Problem（绝不产出无时间戳条目）；
/// - 正文：description 已是纯文本时优先使用；为空才剥离 content 的 HTML（zh-CN 实测为 HTML）；
///   两者都空则跳过该条；标题剥离标签与实体作防御；
/// - 每轮条数 = Math.Clamp(maxItems, 1, 100)（接口上限 100；聚合器今日传 10），每条 Text ≤200 字符；
///   请求 10s 超时、响应体 1MB 上限；
/// - 网络/解析失败只返回 Problem（短中文原因），调用方取消原样传播；
/// - 单实例 TTL 缓存 600s：上游声明缓存延迟约 15 分钟且限流数值未公开，故比金十的 300s 更保守；
///   成功与失败都在 TTL 内复用，只保留一条结果，调用方要得更少时按需裁剪。
/// IHttpClientFactory 由 DI 以单例提供，本类只按请求创建 HttpClient，不持有长生命周期的 HttpClient。
/// </summary>
public sealed class ChainCatcherNewsSource(IHttpClientFactory httpClientFactory) : INewsSource
{
    public const string SourceName = "链捕手";
    private const string UrlTemplate =
        "https://api.chaincatcher.com/v1/open-api/news-flash?type=flash&lang=zh-CN&size={0}";
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const int MaxItems = 100;
    private const int MaxItemChars = 200;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(600);
    // DateTimeOffset.FromUnixTimeMilliseconds 的可表示边界，越界会抛 ArgumentOutOfRangeException
    private const long MinUnixMilliseconds = -62_135_596_800_000;
    private const long MaxUnixMilliseconds = 253_402_300_799_999;
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
        // 失败不缓存：600s 的失败窗口会让一次瞬时抖动把该源藏 10 分钟（第三方源无 SLA，宁可下轮重试）
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

        var url = string.Format(CultureInfo.InvariantCulture, UrlTemplate, maxItems);
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
            if (!root.TryGetProperty("result", out var codeElement)
                || codeElement.ValueKind != JsonValueKind.Number
                || !codeElement.TryGetInt32(out var code))
            {
                return Failure("返回格式异常");
            }
            if (code != 1)
            {
                // HTTP 状态恒 200，失败只体现在 result=0 + 英文 message；message 仅记日志，不透传用户
                Console.WriteLine(
                    $"[News] {Name} 上游返回 result={code}（{LogRedaction.Scrub(ReadString(root, "message"))}）");
                return Failure("服务返回错误");
            }
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return Failure("返回格式异常");
            }

            var parsed = new List<(DateTimeOffset At, ExternalDataItem Item)>();
            foreach (var element in items.EnumerateArray())
            {
                var item = ParseItem(element);
                if (item is not null) parsed.Add(item.Value);
            }
            if (parsed.Count == 0) return Failure("暂无快讯");

            var selected = parsed
                .OrderByDescending(entry => entry.At)
                .Take(maxItems)
                .Select(entry => entry.Item)
                .ToList();
            return new ExternalDataResult(Name, selected, null);
        }
    }

    private static (DateTimeOffset At, ExternalDataItem Item)? ParseItem(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var title = StripHtml(ReadString(element, "title"));
        // description 上游已剥掉 HTML；只有它为空时才回落到 content 的 HTML 文本
        var text = ReadString(element, "description")?.Trim() ?? "";
        if (text.Length == 0) text = StripHtml(ReadString(element, "content"));
        if (text.Length == 0) return null;

        // 时间格式漂移/越界时宁可整源缺席（Problem 可见），也不产出无时间戳的条目
        var at = ParseTimestamp(element);
        if (at is null) return null;

        return (at.Value, new ExternalDataItem(FormatUtc(at.Value), Clamp(title), Clamp(text)));
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element)
    {
        if (!element.TryGetProperty("releaseTimeStamp", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var milliseconds))
        {
            return null;
        }
        if (milliseconds < MinUnixMilliseconds || milliseconds > MaxUnixMilliseconds) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormatUtc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

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
                // 缓存按最近一次请求的条数存放：调用方要得更少时按需裁剪，不越过 INewsSource 的 maxItems 契约
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
