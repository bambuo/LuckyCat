using System.Net;
using System.Text;
using LuckyCat.Contracts;
using LuckyCat.Services;
using LuckyCat.Services.NewsSources;
using Xunit;
using Xunit.Abstractions;

namespace LuckyCat.Tests;

/// <summary>
/// 内置新闻适配器 + 新闻聚合器（Task 14.5 金十数据 / Task 14.6 链捕手）。
/// 夹具手工按线上响应形状编写（金十 status/message/data；链捕手 result/message/data.items），
/// 全部走假 IHttpClientFactory / HttpMessageHandler，默认不触网。
/// </summary>
public sealed class NewsSourcesTests(ITestOutputHelper output)
{
    // 与线上一致：data[].time 为北京时间（UTC+8，无时区标记）；important 线上为 0/1 数字，夹具同时覆盖 true。
    // 条目 1：空标题 + HTML/实体 + important=true；条目 2：超长正文；条目 3：普通；条目 4：应被条数上限裁掉。
    private const string Fixture = """
    {
      "status": 200,
      "message": "OK",
      "data": [
        {
          "id": "20261026022336613800",
          "time": "2026-10-26 02:23:36",
          "important": true,
          "channel": -8200,
          "data": {
            "content": "美联储宣布<b>维持利率不变</b>，并称&nbsp;未来路径取决于通胀&amp;就业数据。",
            "title": "",
            "pic": "",
            "source": "金十数据",
            "source_link": "https://flash.jin10.com/detail/1"
          }
        },
        {
          "id": "20261026022000555800",
          "time": "2026-10-26 02:20:00",
          "important": 0,
          "channel": -8200,
          "data": {
            "content": "据金十数据报道，市场消息人士称，多家大型机构正在重新评估其数字资产配置策略，现货与衍生品市场的资金流向出现明显分化，交易员密切关注即将公布的通胀数据与央行官员讲话，短线波动率可能继续上升，风险资产价格对利率路径的敏感度仍然较高，投资者需要留意流动性变化带来的冲击。据金十数据报道，市场消息人士称，多家大型机构正在重新评估其数字资产配置策略，现货与衍生品市场的资金流向出现明显分化，交易员密切关注即将公布的通胀数据与央行官员讲话，短线波动率可能继续上升，风险资产价格对利率路径的敏感度仍然较高，投资者需要留意流动性变化带来的冲击。",
            "title": "长文快讯",
            "pic": "",
            "source": "金十数据"
          }
        },
        {
          "id": "20261026021000255800",
          "time": "2026-10-26 02:10:00",
          "important": 0,
          "channel": -8200,
          "data": {
            "content": "现货黄金短线拉升10美元，现报4120.50美元/盎司。",
            "title": "黄金<b>异动</b>&amp;快讯",
            "pic": "",
            "source": "金十数据"
          }
        },
        {
          "id": "20261026010000255800",
          "time": "2026-10-26 01:00:00",
          "important": 0,
          "channel": -8200,
          "data": {
            "content": "第四条快讯，应被条数上限裁掉。",
            "title": "第四条",
            "pic": "",
            "source": "金十数据"
          }
        }
      ]
    }
    """;

    // 链捕手线上响应形状：result/message/data.{page,size,total,items[]}，条目为顶层平铺字段；
    // content 为 HTML、description 为已剥标签的纯文本、releaseTimeStamp 为 Unix 毫秒。
    // 条目 1：description 与 HTML content 并存（应优先 description）；条目 2：超长 description；
    // 条目 3：标题含标签/实体；条目 4：应被条数上限裁掉。
    private const string ChainCatcherFixture = """
    {
      "result": 1,
      "message": "处理成功",
      "data": {
        "total": 587,
        "size": 4,
        "page": 1,
        "items": [
          {
            "content": "<p>ChainCatcher 消息，博通 (AVGO.O) 筹集 600 亿美元，<b>用于为 Anthropic 供应芯片</b>。</p>",
            "description": "ChainCatcher 消息，博通 (AVGO.O) 筹集 600 亿美元，用于为 Anthropic 供应芯片。",
            "digest": "",
            "id": 2293656,
            "keywords": "博通,Anthropic,芯片供应",
            "original": false,
            "releaseTimeStamp": 1790914786000,
            "title": "博通筹集 600 亿美元，为 Anthropic 供应芯片",
            "type": "flash",
            "url": "https://www.chaincatcher.com/article/2293656"
          },
          {
            "content": "<p>长文正文（不应被使用）</p>",
            "description": "据 ChainCatcher 报道，多家大型机构正在重新评估其数字资产配置策略，现货与衍生品市场的资金流向出现明显分化，交易员密切关注即将公布的通胀数据与央行官员讲话，短线波动率可能继续上升，风险资产价格对利率路径的敏感度仍然较高，投资者需要留意流动性变化带来的冲击。据 ChainCatcher 报道，多家大型机构正在重新评估其数字资产配置策略，现货与衍生品市场的资金流向出现明显分化，交易员密切关注即将公布的通胀数据与央行官员讲话，短线波动率可能继续上升，风险资产价格对利率路径的敏感度仍然较高，投资者需要留意流动性变化带来的冲击。",
            "digest": "",
            "id": 2293655,
            "keywords": "",
            "original": false,
            "releaseTimeStamp": 1790914700000,
            "title": "长文快讯",
            "type": "flash",
            "url": "https://www.chaincatcher.com/article/2293655"
          },
          {
            "content": "<p>现货黄金短线拉升 10 美元，现报 4120.50 美元/盎司。</p>",
            "description": "现货黄金短线拉升 10 美元，现报 4120.50 美元/盎司。",
            "digest": "",
            "id": 2293654,
            "keywords": "黄金",
            "original": true,
            "releaseTimeStamp": 1790914600000,
            "title": "黄金<b>异动</b>&amp;快讯",
            "type": "flash",
            "url": "https://www.chaincatcher.com/article/2293654"
          },
          {
            "content": "<p>第四条快讯，应被条数上限裁掉。</p>",
            "description": "第四条快讯，应被条数上限裁掉。",
            "digest": "",
            "id": 2293653,
            "keywords": "",
            "original": false,
            "releaseTimeStamp": 1790914500000,
            "title": "第四条",
            "type": "flash",
            "url": "https://www.chaincatcher.com/article/2293653"
          }
        ]
      }
    }
    """;

    [Fact]
    public async Task Fixture_is_parsed_with_caps_html_stripping_and_important_marking()
    {
        var (source, handler) = NewSource((_, _) => Task.FromResult(JsonResponse(Fixture)));

        var result = await source.FetchAsync(3, default);

        Assert.Null(result.Problem);
        Assert.Equal("金十数据", result.Name);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(result.Items, item => item.Title == "第四条");

        // 全部时间归一为 ISO-8601 UTC（Z 结尾）
        Assert.All(result.Items, item => Assert.EndsWith("Z", item.Time));

        var important = result.Items[0];
        Assert.StartsWith("【重要】", important.Text);
        Assert.Equal("", important.Title);
        Assert.Contains("维持利率不变", important.Text);
        Assert.Contains("通胀&就业", important.Text);
        Assert.DoesNotContain("<b>", important.Text);
        Assert.DoesNotContain("&nbsp;", important.Text);
        Assert.DoesNotContain("&amp;", important.Text);
        Assert.True(important.Text.Length <= 200);

        // 超长正文按 200 字符截断（含省略号），重要前缀也计入预算
        var longItem = result.Items[1];
        Assert.Equal("长文快讯", longItem.Title);
        Assert.Equal(200, longItem.Text.Length);
        Assert.EndsWith("…", longItem.Text);

        Assert.DoesNotContain("【重要】", result.Items[2].Text);
        Assert.Contains("黄金", result.Items[2].Title);
        Assert.Contains("&快讯", result.Items[2].Title);
        Assert.DoesNotContain("<b>", result.Items[2].Title);
    }

    [Fact]
    public async Task Numeric_important_flag_is_marked()
    {
        // 线上 important 实际为 0/1 数字（用户实测记录为布尔），两种形状都必须按「重要」处理
        const string body = """
        {"status":200,"message":"OK","data":[{"id":"1","time":"2026-10-26 02:23:36","important":1,"data":{"content":"央行降准0.5个百分点","title":""}}]}
        """;
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse(body)));

        var result = await source.FetchAsync(10, default);

        Assert.StartsWith("【重要】", Assert.Single(result.Items).Text);
    }

    [Fact]
    public async Task Time_is_parsed_as_utc_plus_8_not_host_local_or_utc()
    {
        const string body = """
        {"status":200,"message":"OK","data":[{"id":"1","time":"2026-10-26 02:23:36","important":0,"data":{"content":"测试","title":""}}]}
        """;
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse(body)));

        var result = await source.FetchAsync(10, default);

        // 北京时间 02:23:36 = 前一日 18:23:36Z；本机为 UTC+7，若按本地时区解析会得到 19:23:36Z。
        // 若按 UTC 解析会得到 2026-10-26T02:23:36Z。两种错法都会让此断言失败。
        Assert.Equal("2026-10-25T18:23:36Z", Assert.Single(result.Items).Time);
    }

    [Fact]
    public async Task Item_without_data_is_skipped()
    {
        const string body = """
        {"status":200,"message":"OK","data":[
          {"id":"1","time":"2026-10-26 02:00:00","important":0},
          {"id":"2","time":"2026-10-26 01:00:00","important":0,"data":{"content":"有效快讯","title":""}}
        ]}
        """;
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse(body)));

        var result = await source.FetchAsync(10, default);

        var item = Assert.Single(result.Items);
        Assert.Equal("有效快讯", item.Text);
        Assert.Equal("2026-10-25T17:00:00Z", item.Time);
    }

    [Fact]
    public async Task Http_500_returns_problem_without_throwing()
    {
        var (source, _) = NewSource((_, _) => Task.FromResult(
            JsonResponse("""{"status":500,"message":"error","data":[]}""", HttpStatusCode.InternalServerError)));

        var result = await source.FetchAsync(10, default);

        Assert.Empty(result.Items);
        Assert.NotNull(result.Problem);
        Assert.Contains("500", result.Problem);
        Assert.DoesNotContain("flash-api", result.Problem);
    }

    [Fact]
    public async Task Timeout_returns_problem_without_throwing()
    {
        var (source, _) = NewSource((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out")));

        var result = await source.FetchAsync(10, default);

        Assert.Empty(result.Items);
        Assert.NotNull(result.Problem);
        Assert.Contains("超时", result.Problem);
    }

    [Fact]
    public async Task Malformed_json_returns_problem_without_throwing()
    {
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse("{ not-json")));

        var result = await source.FetchAsync(10, default);

        Assert.Empty(result.Items);
        Assert.NotNull(result.Problem);
        Assert.Contains("JSON", result.Problem);
    }

    [Fact]
    public async Task Empty_data_returns_problem_without_throwing()
    {
        var (source, _) = NewSource((_, _) => Task.FromResult(
            JsonResponse("""{"status":200,"message":"OK","data":[]}""")));

        var result = await source.FetchAsync(10, default);

        Assert.Empty(result.Items);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_is_not_converted_to_problem()
    {
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse(Fixture)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync(10, cts.Token));
    }

    [Fact]
    public async Task Repeated_calls_within_ttl_use_cache()
    {
        var (source, handler) = NewSource((_, _) => Task.FromResult(JsonResponse(Fixture)));

        var first = await source.FetchAsync(10, default);
        var second = await source.FetchAsync(10, default);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(first.Items.Count, second.Items.Count);
        Assert.Equal(first.FetchedAtUtc, second.FetchedAtUtc);
    }

    [Fact]
    public async Task Cached_result_is_trimmed_to_a_smaller_max_items_request()
    {
        var (source, handler) = NewSource((_, _) => Task.FromResult(JsonResponse(Fixture)));

        var wide = await source.FetchAsync(10, default);
        var narrow = await source.FetchAsync(2, default);

        Assert.Equal(1, handler.Calls);
        Assert.True(wide.Items.Count > 2);
        Assert.Equal(2, narrow.Items.Count);
        Assert.Equal(wide.Items.Take(2).Select(i => i.Text), narrow.Items.Select(i => i.Text));
    }

    [Fact]
    public async Task Item_with_unparseable_time_is_skipped_instead_of_timeless()
    {
        // 时间格式漂移（如带毫秒/时区后缀）时不得产出 Time="" 的条目：那会让跨源去重退化、
        // 新快讯在排序里沉底。整批都无法解析时如实报 Problem，漂移因此可见。
        const string oneBad = """
        {"status":200,"message":"OK","data":[
          {"id":"1","time":"2026-10-26 02:23:36.123","important":0,"data":{"content":"带毫秒","title":""}},
          {"id":"2","time":"2026-10-26 02:20:00","important":0,"data":{"content":"正常条目","title":""}}]}
        """;
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse(oneBad)));

        var result = await source.FetchAsync(10, default);

        var item = Assert.Single(result.Items);
        Assert.Contains("正常条目", item.Text);
        Assert.Equal("2026-10-25T18:20:00Z", item.Time);

        const string allBad = """
        {"status":200,"message":"OK","data":[{"id":"1","time":"2026/10/26 02:23:36","important":0,"data":{"content":"全都解析不了","title":""}}]}
        """;
        var (badSource, _) = NewSource((_, _) => Task.FromResult(JsonResponse(allBad)));
        var bad = await badSource.FetchAsync(10, default);
        Assert.Empty(bad.Items);
        Assert.NotNull(bad.Problem);
    }

    [Fact]
    public async Task Title_only_item_is_kept()
    {
        const string body = """
        {"status":200,"message":"OK","data":[{"id":"1","time":"2026-10-26 02:23:36","important":0,"data":{"content":"","title":"仅标题快讯"}}]}
        """;
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse(body)));

        var result = await source.FetchAsync(10, default);

        Assert.Contains("仅标题快讯", Assert.Single(result.Items).Text);
    }

    [Fact]
    public async Task Float_important_flag_is_marked()
    {
        // 线上曾观察到 1.0 这类浮点编码；只认整型会让它静默失去【重要】标记
        const string body = """
        {"status":200,"message":"OK","data":[{"id":"1","time":"2026-10-26 02:23:36","important":1.0,"data":{"content":"浮点重要","title":""}}]}
        """;
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse(body)));

        var result = await source.FetchAsync(10, default);

        Assert.StartsWith("【重要】", Assert.Single(result.Items).Text);
    }

    [Fact]
    public async Task Bom_prefixed_body_is_parsed()
    {
        var (source, _) = NewSource((_, _) => Task.FromResult(JsonResponse("\uFEFF" + Fixture)));

        var result = await source.FetchAsync(10, default);

        Assert.Null(result.Problem);
        Assert.NotEmpty(result.Items);
    }

    [Fact]
    public async Task ChainCatcher_fixture_uses_description_caps_count_and_normalizes_ms_timestamp()
    {
        var (source, handler) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(ChainCatcherFixture)));

        var result = await source.FetchAsync(3, default);

        Assert.Null(result.Problem);
        Assert.Equal("链捕手", result.Name);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(result.Items, item => item.Title == "第四条");
        Assert.All(result.Items, item => Assert.EndsWith("Z", item.Time));

        var first = result.Items[0];
        Assert.Equal("博通筹集 600 亿美元，为 Anthropic 供应芯片", first.Title);
        // description（纯文本）优先于 HTML content；content 中的标签不得漏进 Text
        Assert.Equal("ChainCatcher 消息，博通 (AVGO.O) 筹集 600 亿美元，用于为 Anthropic 供应芯片。", first.Text);
        Assert.DoesNotContain("<p>", first.Text);
        // 1790914786000 ms ÷ 1000 = 1790914786 s；1790914786 = 20728×86400 + 101986，
        // 101986 s = 28h19m46s（跨日取次日 04:19:46），epoch 后第 20728 天 = 2026-10-02。
        Assert.Equal("2026-10-02T04:19:46Z", first.Time);

        // 超长 description 按 200 字符截断（省略号计入），且不会回落到 HTML content
        var longItem = result.Items[1];
        Assert.Equal("长文快讯", longItem.Title);
        Assert.Equal(200, longItem.Text.Length);
        Assert.EndsWith("…", longItem.Text);
        Assert.DoesNotContain("不应被使用", longItem.Text);

        var third = result.Items[2];
        // 标签以空格替换，避免相邻文本粘连；只断言标签/实体已被剥离
        Assert.Contains("黄金", third.Title);
        Assert.Contains("异动", third.Title);
        Assert.Contains("&快讯", third.Title);
        Assert.DoesNotContain("<b>", third.Title);
        Assert.DoesNotContain("&amp;", third.Title);
    }

    [Fact]
    public async Task ChainCatcher_html_only_item_uses_stripped_content()
    {
        const string body = """
        {"result":1,"message":"处理成功","data":{"total":1,"size":1,"page":1,"items":[
          {"content":"<p>快讯&nbsp;<b>HTML</b> 正文</p>","description":"","digest":"","id":1,"keywords":"","original":false,"releaseTimeStamp":1790914786000,"title":"仅 HTML","type":"flash","url":"https://www.chaincatcher.com/article/1"}]}}
        """;
        var (source, _) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(body)));

        var result = await source.FetchAsync(10, default);

        var item = Assert.Single(result.Items);
        Assert.Equal("快讯 HTML 正文", item.Text);
        Assert.DoesNotContain("<b>", item.Text);
        Assert.DoesNotContain("&nbsp;", item.Text);
    }

    [Fact]
    public async Task ChainCatcher_result_zero_returns_problem_without_leaking_english_message()
    {
        // 上游 HTTP 状态恒 200；result=0 时 message 是英文技术文案，只进日志、不进入 Problem
        var (source, _) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(
            """{"result":0,"message":"size cannot exceed 100","data":null}""")));

        var result = await source.FetchAsync(10, default);

        Assert.Empty(result.Items);
        Assert.NotNull(result.Problem);
        Assert.DoesNotContain("cannot exceed", result.Problem);
        Assert.DoesNotContain("size", result.Problem);
    }

    [Fact]
    public async Task ChainCatcher_malformed_500_and_timeout_return_problem_without_throwing()
    {
        var (malformed, _) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse("<html>not json</html>")));
        var malformedResult = await malformed.FetchAsync(10, default);
        Assert.Empty(malformedResult.Items);
        Assert.NotNull(malformedResult.Problem);
        Assert.Contains("JSON", malformedResult.Problem);

        var (failed, _) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(
            """{"result":1,"message":"处理成功","data":{"items":[]}}""", HttpStatusCode.InternalServerError)));
        var failedResult = await failed.FetchAsync(10, default);
        Assert.Empty(failedResult.Items);
        Assert.NotNull(failedResult.Problem);
        Assert.Contains("500", failedResult.Problem);

        var (timeout, _) = NewChainCatcherSource((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out")));
        var timeoutResult = await timeout.FetchAsync(10, default);
        Assert.Empty(timeoutResult.Items);
        Assert.NotNull(timeoutResult.Problem);
        Assert.Contains("超时", timeoutResult.Problem);
    }

    [Fact]
    public async Task ChainCatcher_caller_cancellation_propagates_and_is_not_converted_to_problem()
    {
        var (source, _) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(ChainCatcherFixture)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync(10, cts.Token));
    }

    [Fact]
    public async Task ChainCatcher_repeated_calls_within_ttl_use_cache()
    {
        var (source, handler) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(ChainCatcherFixture)));

        var first = await source.FetchAsync(10, default);
        var second = await source.FetchAsync(10, default);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(first.Items.Count, second.Items.Count);
        Assert.Equal(first.FetchedAtUtc, second.FetchedAtUtc);
    }

    [Fact]
    public async Task ChainCatcher_cached_result_is_trimmed_to_a_smaller_max_items_request()
    {
        var (source, handler) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(ChainCatcherFixture)));

        var wide = await source.FetchAsync(10, default);
        var narrow = await source.FetchAsync(2, default);

        Assert.Equal(1, handler.Calls);
        Assert.True(wide.Items.Count > 2);
        Assert.Equal(2, narrow.Items.Count);
        Assert.Equal(wide.Items.Take(2).Select(i => i.Text), narrow.Items.Select(i => i.Text));
    }

    [Fact]
    public async Task ChainCatcher_item_with_unparseable_timestamp_is_skipped()
    {
        // 字符串时间与超出 DateTimeOffset 范围的毫秒数都必须跳过，不得产出 Time="" 的条目
        const string oneBad = """
        {"result":1,"message":"处理成功","data":{"total":3,"size":3,"page":1,"items":[
          {"content":"<p>字符串时间</p>","description":"字符串时间","digest":"","id":1,"keywords":"","original":false,"releaseTimeStamp":"1790914786000","title":"字符串时间","type":"flash","url":"https://www.chaincatcher.com/article/1"},
          {"content":"<p>越界时间</p>","description":"越界时间","digest":"","id":2,"keywords":"","original":false,"releaseTimeStamp":999999999999999999,"title":"越界时间","type":"flash","url":"https://www.chaincatcher.com/article/2"},
          {"content":"<p>正常条目</p>","description":"正常条目","digest":"","id":3,"keywords":"","original":false,"releaseTimeStamp":1790914700000,"title":"正常条目","type":"flash","url":"https://www.chaincatcher.com/article/3"}]}}
        """;
        var (source, _) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(oneBad)));

        var result = await source.FetchAsync(10, default);

        var item = Assert.Single(result.Items);
        Assert.Equal("正常条目", item.Text);
        // 1790914700000 ms = 1790914700 s，比 1790914786 s 早 86 秒 → 04:18:20Z
        Assert.Equal("2026-10-02T04:18:20Z", item.Time);

        const string allBad = """
        {"result":1,"message":"处理成功","data":{"total":2,"size":2,"page":1,"items":[
          {"content":"<p>空时间</p>","description":"空时间","digest":"","id":1,"keywords":"","original":false,"releaseTimeStamp":null,"title":"空时间","type":"flash","url":"https://www.chaincatcher.com/article/1"},
          {"content":"<p>字符串时间</p>","description":"字符串时间","digest":"","id":2,"keywords":"","original":false,"releaseTimeStamp":"1790914786000","title":"字符串时间","type":"flash","url":"https://www.chaincatcher.com/article/2"}]}}
        """;
        var (badSource, _) = NewChainCatcherSource((_, _) => Task.FromResult(JsonResponse(allBad)));
        var bad = await badSource.FetchAsync(10, default);
        Assert.Empty(bad.Items);
        Assert.NotNull(bad.Problem);
    }

    [Fact]
    public async Task ChainCatcher_request_size_tracks_max_items_and_never_exceeds_100()
    {
        string? uri = null;
        var (source, _) = NewChainCatcherSource((request, _) =>
        {
            uri = request.RequestUri!.ToString();
            return Task.FromResult(JsonResponse(ChainCatcherFixture));
        });

        await source.FetchAsync(10, default);

        Assert.NotNull(uri);
        Assert.Contains("size=10", uri);
        Assert.Contains("type=flash", uri);
        Assert.Contains("lang=zh-CN", uri);

        string? wideUri = null;
        var (wideSource, _) = NewChainCatcherSource((request, _) =>
        {
            wideUri = request.RequestUri!.ToString();
            return Task.FromResult(JsonResponse(ChainCatcherFixture));
        });

        await wideSource.FetchAsync(500, default);

        Assert.NotNull(wideUri);
        Assert.Contains("size=100", wideUri);
        Assert.DoesNotContain("size=101", wideUri);
    }

    [Fact]
    public async Task Aggregator_merges_jin10_chaincatcher_and_configured_sources()
    {
        const string jin10Body = """
        {"status":200,"message":"OK","data":[{"id":"1","time":"2026-10-02 12:19:46","important":0,"data":{"content":"来自金十的同一条","title":"同一条快讯"}}]}
        """;
        const string chainCatcherBody = """
        {"result":1,"message":"处理成功","data":{"total":2,"size":2,"page":1,"items":[
          {"content":"<p>来自链捕手的同一条</p>","description":"来自链捕手的同一条","digest":"","id":1,"keywords":"","original":false,"releaseTimeStamp":1790914786000,"title":"同一条快讯","type":"flash","url":"https://www.chaincatcher.com/article/1"},
          {"content":"<p>链捕手独有</p>","description":"链捕手独有","digest":"","id":2,"keywords":"","original":false,"releaseTimeStamp":1790914700000,"title":"链捕手独有","type":"flash","url":"https://www.chaincatcher.com/article/2"}]}}
        """;
        var router = new StubHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.Host == "api.chaincatcher.com" ? chainCatcherBody : jin10Body)));
        var builtInFactory = new StubFactory(router);
        var configured = new ExternalDataSourceService(new StubFactory(new StubHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"items":[{"title":"配置源独有","time":"t-1","content":"配置源"}]}""")))));
        var aggregator = new CryptoNewsAggregator(
            [new Jin10NewsSource(builtInFactory), new ChainCatcherNewsSource(builtInFactory)], configured);

        var results = await aggregator.FetchAsync(
            [new ExternalDataSource { Name = "wire", Url = $"https://wire.example/{Guid.NewGuid():N}", DataPath = "items" }],
            default);

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { "wire", "金十数据", "链捕手" }, results.Select(result => result.Name));
        // 配置源 1 条 + 金十 1 条 + 链捕手 2 条，其中「同一条快讯 / 2026-10-02T04:19:46Z」跨源重复只保留先到的金十
        Assert.Equal(3, results.Sum(result => result.Items.Count));
        Assert.Single(results.SelectMany(result => result.Items), item => item.Title == "同一条快讯");
        Assert.DoesNotContain(results[2].Items, item => item.Title == "同一条快讯");
        Assert.Contains(results[2].Items, item => item.Title == "链捕手独有");
        Assert.Equal("2026-10-02T04:19:46Z", results[1].Items[0].Time);
    }

    [Fact]
    public async Task Live_chaincatcher_smoke_when_enabled_by_environment()
    {
        // 默认不触网：仅在显式设置 LUCKYCAT_LIVE_CHAINCATCHER=1 时执行（不 Skip，保持 0 skipped 基线）。
        if (Environment.GetEnvironmentVariable("LUCKYCAT_LIVE_CHAINCATCHER") != "1") return;

        var source = new ChainCatcherNewsSource(new StubFactory(new HttpClientHandler()));
        var result = await source.FetchAsync(10, default);
        output.WriteLine($"live items={result.Items.Count} first={result.Items.FirstOrDefault()?.Time} problem={result.Problem}");

        Assert.Null(result.Problem);
        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, item => Assert.EndsWith("Z", item.Time));
    }

    [Fact]
    public async Task Aggregator_merges_configured_and_builtin_and_dedupes_title_time()
    {
        var configured = new ExternalDataSourceService(new StubFactory(new StubHandler((_, _) =>
            Task.FromResult(JsonResponse("""
            {"items":[
              {"title":"同一条快讯","time":"t-1","content":"来自配置源"},
              {"title":"另一条","time":"t-2","content":"配置源独有"}
            ]}
            """)))));
        var builtIn = new FakeNewsSource("内置源",
            new ExternalDataItem("t-1", "同一条快讯", "来自内置源（重复）"),
            new ExternalDataItem("t-3", "第三条", "内置源独有"));
        var aggregator = new CryptoNewsAggregator([builtIn], configured);

        var results = await aggregator.FetchAsync(
            [new ExternalDataSource { Name = "wire", Url = $"https://wire.example/{Guid.NewGuid():N}", DataPath = "items" }],
            default);

        Assert.Equal(2, results.Count);
        Assert.Equal("wire", results[0].Name);
        Assert.Equal("内置源", results[1].Name);

        // 配置源 2 条 + 内置源 1 条；「同一条快讯 / t-1」只保留一次（先到先得）
        Assert.Equal(3, results.Sum(result => result.Items.Count));
        Assert.Single(results.SelectMany(result => result.Items), item => item.Title == "同一条快讯");
        Assert.Contains(results[0].Items, item => item.Title == "另一条");
        Assert.Contains(results[1].Items, item => item.Title == "第三条");
        Assert.DoesNotContain(results[1].Items, item => item.Title == "同一条快讯");
    }

    [Fact]
    public async Task Aggregator_dedupes_by_text_prefix_when_titles_are_empty()
    {
        var emptyFeed = new ExternalDataSourceService(new StubFactory(new StubHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"items":[]}""")))));
        var first = new FakeNewsSource("A", new ExternalDataItem("t-9", "", new string('X', 50) + "甲"));
        var second = new FakeNewsSource("B", new ExternalDataItem("t-9", "", new string('X', 50) + "乙"));
        var aggregator = new CryptoNewsAggregator([first, second], emptyFeed);

        var results = await aggregator.FetchAsync([], default);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results.Sum(result => result.Items.Count));
    }

    [Fact]
    public async Task Failing_builtin_leaves_other_results_intact()
    {
        var configured = new ExternalDataSourceService(new StubFactory(new StubHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"items":[{"title":"配置快讯","time":"t-1","content":"正常"}]}""")))));
        var aggregator = new CryptoNewsAggregator(
            [new FailingNewsSource(), new ThrowingNewsSource()], configured);

        var results = await aggregator.FetchAsync(
            [new ExternalDataSource { Name = "wire", Url = $"https://wire.example/{Guid.NewGuid():N}", DataPath = "items" }],
            default);

        Assert.Equal(3, results.Count);
        Assert.Single(results[0].Items);
        Assert.Equal("配置快讯", results[0].Items[0].Title);
        Assert.Contains(results, result => result.Name == "坏源" && result.Problem == "拉取失败");
        Assert.Contains(results, result => result.Name == "抛异常源" && result.Problem is not null);
    }

    [Fact]
    public async Task Live_jin10_smoke_when_enabled_by_environment()
    {
        // 默认不触网：仅在显式设置 LUCKYCAT_LIVE_NEWS=1 时执行（不 Skip，保持 0 skipped 基线）。
        if (Environment.GetEnvironmentVariable("LUCKYCAT_LIVE_NEWS") != "1") return;

        var source = new Jin10NewsSource(new StubFactory(new HttpClientHandler()));
        var result = await source.FetchAsync(10, default);
        output.WriteLine($"live items={result.Items.Count} first={result.Items.FirstOrDefault()?.Time} problem={result.Problem}");

        Assert.Null(result.Problem);
        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, item => Assert.EndsWith("Z", item.Time));
    }

    private static (Jin10NewsSource Source, StubHandler Handler) NewSource(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        return (new Jin10NewsSource(new StubFactory(handler)), handler);
    }

    private static (ChainCatcherNewsSource Source, StubHandler Handler) NewChainCatcherSource(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        return (new ChainCatcherNewsSource(new StubFactory(handler)), handler);
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeNewsSource(string name, params ExternalDataItem[] items) : INewsSource
    {
        public string Name => name;

        public Task<ExternalDataResult> FetchAsync(int maxItems, CancellationToken ct) =>
            Task.FromResult(new ExternalDataResult(Name, items.Take(maxItems).ToList(), null, DateTime.UtcNow));
    }

    private sealed class FailingNewsSource : INewsSource
    {
        public string Name => "坏源";

        public Task<ExternalDataResult> FetchAsync(int maxItems, CancellationToken ct) =>
            Task.FromResult(new ExternalDataResult(Name, [], "拉取失败"));
    }

    private sealed class ThrowingNewsSource : INewsSource
    {
        public string Name => "抛异常源";

        public Task<ExternalDataResult> FetchAsync(int maxItems, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
