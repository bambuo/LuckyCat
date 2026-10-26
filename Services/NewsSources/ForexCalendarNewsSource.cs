using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LuckyCat.Services.NewsSources;

/// <summary>
/// ForexFactory 本周财经日历内置源（Task 14.7）：
/// GET https://nfs.faireconomy.media/ff_calendar_thisweek.json，无鉴权，固定浏览器 User-Agent。
/// 口径：
/// - 响应为数组：<c>{title, country, date(带偏移的 ISO-8601), impact, forecast, previous}</c>；
///   只保留 impact 为 High / Medium 的事件（Low / Holiday 丢弃）；
/// - 时间：date 带偏移（线上为 -04:00），按偏移换算后渲染为 ISO-8601 UTC（Z 结尾）；
///   不可解析的条目跳过；只保留<b>尚未发生</b>的事件（给 30 分钟宽限，进行中的仍可见），
///   按时间<b>升序</b>取最近的 maxItems 条（日历视角：先发生的先看）；本周已无待发生事件时记 Problem；
/// - 展示：Title = 事件名；Text = <c>国家=… 预期=… 前值=…（影响=…）</c>，空字段渲染为 <c>-</c>；
/// - 每轮最多 <see cref="MaxItems"/> 条、每条 Title/Text ≤200 字符；
///   请求 10s 超时（linked CTS 覆盖响应体读取）、响应体 1MB 上限、BOM 容忍；
/// - 网络/解析失败只返回固定短中文 Problem，不抛异常；调用方取消原样传播；
/// - 单实例 TTL 缓存 600s；失败不缓存（与链捕手修复同口径）；调用方要得更少时裁剪缓存切片。
/// IHttpClientFactory 由 DI 以单例提供，本类只按请求创建 HttpClient，不持有长生命周期的 HttpClient。
/// </summary>
public sealed class ForexCalendarNewsSource(
    IHttpClientFactory httpClientFactory,
    Func<DateTimeOffset>? now = null) : INewsSource
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public const string SourceName = "财经日历";
    private const string Url = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const int MaxItems = 50;
    private const int MaxItemChars = 200;
    private const int MaxBodyBytes = 1024 * 1024;
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
            if (root.ValueKind != JsonValueKind.Array) return Failure("返回格式异常");

            var parsed = new List<(DateTimeOffset At, ExternalDataItem Item)>();
            foreach (var element in root.EnumerateArray())
            {
                var item = ParseEvent(element);
                if (item is not null) parsed.Add(item.Value);
            }
            if (parsed.Count == 0) return Failure("暂无财经事件");

            // 前向日历：只保留尚未发生（含刚过去 30 分钟内的进行中事件）的 High/Medium 事件，
            // 否则周五还会看到周一的非农，变成过期噪音。
            var since = _now().AddMinutes(-30);
            var selected = parsed
                .Where(entry => entry.At >= since)
                .OrderBy(entry => entry.At)
                .Take(maxItems)
                .Select(entry => entry.Item)
                .ToList();
            if (selected.Count == 0) return Failure("本周暂无待发生的高影响事件");
            return new ExternalDataResult(Name, selected, null);
        }
    }

    private static (DateTimeOffset At, ExternalDataItem Item)? ParseEvent(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var impact = ReadString(element, "impact")?.Trim() ?? "";
        if (impact is not ("High" or "Medium")) return null;

        var title = StripHtml(ReadString(element, "title"));
        if (title.Length == 0) return null;

        var at = ParseEventTime(ReadString(element, "date"));
        if (at is null) return null;

        // 字段缺失/空串渲染为 '-'；数值本身按原样展示（如 150K、2.1%）
        var text = $"国家={Field(element, "country")} 预期={Field(element, "forecast")}"
            + $" 前值={Field(element, "previous")}（影响={impact}）";
        return (at.Value, new ExternalDataItem(FormatUtc(at.Value), Clamp(title), Clamp(text)));
    }

    // 带偏移的 ISO-8601：TryParse 按偏移换算，AdjustToUniversal 归一为 UTC（不落机器本地时区）
    private static DateTimeOffset? ParseEventTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    private static string Field(JsonElement element, string name)
    {
        var value = StripHtml(ReadString(element, name));
        return value.Length == 0 ? "-" : value;
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
