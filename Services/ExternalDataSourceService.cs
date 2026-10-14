using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>外部数据源的单条内容（新闻/事件等）。</summary>
public record ExternalDataItem(string Time, string Title, string Text);

/// <summary>外部数据源的一轮拉取结果；Problem 非空表示该源本轮缺席（网络失败 / 取值路径取空 / 解析失败）。</summary>
public record ExternalDataResult(string Name, IReadOnlyList<ExternalDataItem> Items, string? Problem, DateTime? FetchedAtUtc = null);

/// <summary>
/// 外部数据源真实拉取（策略工作室 AI 测试使用）：GET/POST 并行抓取 → 自动识别内容类型（JSON 按取值路径 / RSS-XML 自动解析）
/// → 提取为条目标题 + 时间 + 摘要（剥离 HTML、按上限截断）→ 注入用户提示词。
/// 口径（用户确认）：每源最多 10 条、每条 ≤200 字符；按「刷新间隔」秒做内存缓存；单源 10s 超时、响应体 1MB 上限；
/// 默认 User-Agent（部分站点无 UA 会 403），自定义请求头优先。
/// </summary>
public class ExternalDataSourceService(IHttpClientFactory httpClientFactory)
{
    private const int MaxItems = 10;
    private const int MaxItemChars = 200;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, (DateTime At, ExternalDataResult Result)> Cache = new();

    public async Task<IReadOnlyList<ExternalDataResult>> FetchAllAsync(IEnumerable<ExternalDataSource> sources, CancellationToken ct)
    {
        var tasks = sources.Select(s => FetchAsync(s, ct)).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<ExternalDataResult> FetchAsync(ExternalDataSource source, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(source.Name) ? source.Url : source.Name;
        if (string.IsNullOrWhiteSpace(source.Url) || !Uri.TryCreate(source.Url, UriKind.Absolute, out _))
        {
            return new ExternalDataResult(name, [], "接口地址无效");
        }

        // 「刷新间隔」生效为内存缓存 TTL（同一源在 TTL 内复用以避免重复请求）
        var key = $"{name}|{source.Url}";
        var ttl = TimeSpan.FromSeconds(Math.Clamp(source.RefreshSecs, 5, 3600));
        if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ttl)
        {
            return cached.Result;
        }

        // 突发超时的重试（最多 3 次、退避 1/2/3 秒）由全局弹性管道负责（Program.cs · AddStandardResilienceHandler）
        ExternalDataResult result;
        try
        {
            result = await FetchCoreAsync(source, name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new ExternalDataResult(name, [], "拉取超时");
        }
        catch (Exception ex)
        {
            result = new ExternalDataResult(name, [], ex is HttpRequestException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        }

        if (result.Problem is not null)
        {
            Console.WriteLine($"[ExternalData] {name} 本轮缺席（{result.Problem}）");
        }

        result = result with { FetchedAtUtc = DateTime.UtcNow };
        Cache[key] = (DateTime.UtcNow, result);
        return result;
    }

    private async Task<ExternalDataResult> FetchCoreAsync(ExternalDataSource source, string name, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            string.Equals(source.Method, "POST", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get,
            source.Url);

        // 默认 User-Agent：部分数据源（如宏观日历）无 UA 会 403；自定义请求头优先
        if (!source.Headers.Any(h => string.Equals(h.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        }
        foreach (var header in source.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Key)))
        {
            request.Headers.TryAddWithoutValidation(header.Key.Trim(), header.Value ?? "");
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // 抛出后由弹性管道按「瞬时错误」判断是否重试（5xx/408/429 重试；404 等直接失败）
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        var body = await ReadLimitedAsync(response, ct).ConfigureAwait(false);
        var trimmed = body.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');

        return trimmed.StartsWith('<')
            ? ParseXml(name, body)
            : ParseJson(name, source.DataPath, body);
    }

    // 响应体读取（1MB 上限，避免异常大响应拖垮测试）
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
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // —— JSON：按取值路径提取（数组 → 前 N 条；标量/对象 → 单条；路径未命中 → 本轮缺席）——

    private static ExternalDataResult ParseJson(string name, string? dataPath, string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            return new ExternalDataResult(name, [], $"JSON 解析失败：{ex.Message}");
        }

        using (doc)
        {
            var node = doc.RootElement;
            if (!string.IsNullOrWhiteSpace(dataPath))
            {
                if (!TryResolvePath(node, dataPath.Trim(), out node))
                {
                    return new ExternalDataResult(name, [], "取值路径取空");
                }
            }

            var items = node.ValueKind switch
            {
                JsonValueKind.Array => node.EnumerateArray().Take(MaxItems).Select((el, i) => RenderJsonItem(el, i)).ToList(),
                _ => [RenderJsonItem(node, 0)],
            };
            return items.Count == 0
                ? new ExternalDataResult(name, [], "取值路径取空")
                : new ExternalDataResult(name, items, null);
        }
    }

    // 点分路径，支持数组下标（如 data.items.0.title）
    private static bool TryResolvePath(JsonElement root, string path, out JsonElement result)
    {
        result = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index < 0 || index >= result.GetArrayLength()) return false;
                result = result[index];
                continue;
            }
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(segment, out var next)) return false;
            result = next;
        }
        return true;
    }

    private static ExternalDataItem RenderJsonItem(JsonElement element, int index)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var title = FirstString(element, "title", "name", "headline") ?? $"条目 {index + 1}";
            var time = FormatTime(FirstString(element, "releaseTimeStamp", "timestamp", "time", "date", "pubDate", "publishTime", "createdAt"));
            var text = FirstString(element, "description", "content", "summary", "text");
            if (text is null)
            {
                // 无可读文本字段时，退化为对象的紧凑键值渲染
                text = string.Join("；", element.EnumerateObject().Take(6).Select(p => $"{p.Name}: {CompactJson(p.Value)}"));
            }
            return new ExternalDataItem(time, Clamp(title), Clamp(StripHtml(text)));
        }

        var scalar = CompactJson(element);
        return new ExternalDataItem("", Clamp($"条目 {index + 1}"), Clamp(StripHtml(scalar)));
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return null;
    }

    private static string CompactJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();

    // 毫秒/秒时间戳与常见日期串归一为「MM-dd HH:mm」（本地时区）；无法识别时原样返回（截断）
    private static string FormatTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            try
            {
                var moment = raw.Length >= 12
                    ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                    : DateTimeOffset.FromUnixTimeSeconds(number);
                return moment.ToLocalTime().ToString("MM-dd HH:mm");
            }
            catch (ArgumentOutOfRangeException)
            {
                return "";
            }
        }
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed.ToLocalTime().ToString("MM-dd HH:mm");
        }
        return Clamp(raw);
    }

    // —— RSS / Atom：自动解析条目（title + 时间 + 描述）——

    private static ExternalDataResult ParseXml(string name, string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body, LoadOptions.None);
        }
        catch (Exception ex)
        {
            return new ExternalDataResult(name, [], $"XML 解析失败：{ex.Message}");
        }

        var entries = document.Descendants()
            .Where(e => e.Name.LocalName is "item" or "entry")
            .Take(MaxItems)
            .Select((entry, i) =>
            {
                var title = ChildText(entry, "title") ?? $"条目 {i + 1}";
                var time = FormatTime(ChildText(entry, "pubDate") ?? ChildText(entry, "published") ?? ChildText(entry, "updated"));
                var text = ChildText(entry, "description") ?? ChildText(entry, "summary") ?? ChildText(entry, "encoded") ?? ChildText(entry, "content");
                return new ExternalDataItem(time, Clamp(title), Clamp(StripHtml(text)));
            })
            .ToList();

        return entries.Count == 0
            ? new ExternalDataResult(name, [], "未解析到 RSS/Atom 条目")
            : new ExternalDataResult(name, entries, null);
    }

    private static string? ChildText(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    // —— 文本清洗 ——

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    private static string StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = HtmlTagRegex.Replace(value, " ");
        text = text
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Clamp(string value) =>
        value.Length <= MaxItemChars ? value : value[..MaxItemChars] + "…";
}
