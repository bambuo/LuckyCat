using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Models;

namespace LuckyCat.Services;

public class VerificationRunner(ChatClientFactory factory)
{
    public const string CheckBasic = "basic";
    public const string CheckStreaming = "streaming";
    public const string CheckTools = "tools";
    public const string CheckJson = "json";

    private static readonly string[] CheckIds = [CheckBasic, CheckStreaming, CheckTools, CheckJson];
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly AIFunction WeatherTool = AIFunctionFactory.Create(
        GetWeather,
        name: "get_weather",
        description: "Get the current weather of a city");

    public async IAsyncEnumerable<VerificationEvent> RunAsync(
        Provider provider,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IChatClient client = null!;
        string? createError = null;
        try
        {
            client = factory.Create(provider);
        }
        catch (Exception ex)
        {
            createError = ex.Message;
        }

        var passed = 0;
        var failed = 0;

        if (createError is not null)
        {
            // 客户端创建失败：仍产出完整事件序列，SSE 契约对任何失败均成立
            foreach (var checkId in CheckIds)
            {
                yield return new VerificationEvent("start", Check: checkId);
                yield return new VerificationEvent("result", Check: checkId, Status: "fail",
                    ElapsedMs: 0, Detail: Truncate(createError));
                failed++;
            }
            yield return new VerificationEvent("done", Passed: 0, Failed: failed);
            yield break;
        }

        try
        {
            foreach (var checkId in CheckIds)
            {
                yield return new VerificationEvent("start", Check: checkId);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(CheckTimeout);

                await foreach (var evt in RunCheckAsync(checkId, client, timeoutCts.Token))
                {
                    if (evt is { Type: "result", Status: "pass" }) passed++;
                    else if (evt is { Type: "result" }) failed++;
                    yield return evt;
                }
            }

            yield return new VerificationEvent("done", Passed: passed, Failed: failed);
        }
        finally
        {
            if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private IAsyncEnumerable<VerificationEvent> RunCheckAsync(string checkId, IChatClient client, CancellationToken ct) => checkId switch
    {
        CheckBasic => RunBasicAsync(client, ct),
        CheckStreaming => RunStreamingAsync(client, ct),
        CheckTools => RunToolsAsync(client, ct),
        CheckJson => RunJsonAsync(client, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(checkId), checkId, "未知检查项"),
    };

    // —— 1. 基础连通 ——
    private async IAsyncEnumerable<VerificationEvent> RunBasicAsync(IChatClient client, [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string status, detail;
        string? evidence = null;

        try
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with exactly: OK")],
                new ChatOptions { MaxOutputTokens = 128 },
                ct);

            var text = response.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                status = "fail";
                detail = "模型未返回任何文本";
            }
            else
            {
                status = "pass";
                var usage = response.Usage is { } u
                    ? $"，tokens 入 {u.InputTokenCount ?? 0} / 出 {u.OutputTokenCount ?? 0}"
                    : "";
                detail = $"回复正常（{sw.ElapsedMilliseconds}ms）{usage}";
                evidence = Truncate(text);
            }
        }
        catch (OperationCanceledException)
        {
            status = "fail";
            detail = "超时(30s)";
        }
        catch (Exception ex)
        {
            status = "fail";
            detail = ex.Message;
        }

        yield return new VerificationEvent("result", Check: CheckBasic, Status: status,
            ElapsedMs: sw.ElapsedMilliseconds, Detail: detail, Evidence: evidence);
    }

    // —— 2. 流式输出（逐 chunk 事件） ——
    private async IAsyncEnumerable<VerificationEvent> RunStreamingAsync(IChatClient client, [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chunks = 0;
        long? ttftMs = null;
        string? error = null;
        var timedOut = false;

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            enumerator = client.GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, "Count from 1 to 5, separated by spaces.")],
                    new ChatOptions { MaxOutputTokens = 128 },
                    ct)
                .GetAsyncEnumerator(ct);
        }
        catch (OperationCanceledException) { timedOut = true; }
        catch (Exception ex) { error = ex.Message; }

        try
        {
            while (!timedOut && error is null)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator!.MoveNextAsync()) break;
                    update = enumerator.Current;
                }
                catch (OperationCanceledException) { timedOut = true; break; }
                catch (Exception ex) { error = ex.Message; break; }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    chunks++;
                    ttftMs ??= sw.ElapsedMilliseconds;
                    yield return new VerificationEvent("chunk", Check: CheckStreaming,
                        Count: chunks, TtftMs: ttftMs);
                }
            }
        }
        finally
        {
            sw.Stop();
            if (enumerator is not null)
            {
                try { await enumerator.DisposeAsync(); } catch { /* 忽略释放异常 */ }
            }
        }

        string status, detail;
        if (timedOut) { status = "fail"; detail = "超时(30s)"; }
        else if (error is not null) { status = "fail"; detail = error; }
        else if (chunks == 0) { status = "fail"; detail = "未收到任何文本 chunk"; }
        else { status = "pass"; detail = $"首 token {ttftMs}ms，共 {chunks} 个 chunk，总耗时 {sw.ElapsedMilliseconds}ms"; }

        yield return new VerificationEvent("result", Check: CheckStreaming, Status: status,
            ElapsedMs: sw.ElapsedMilliseconds, Detail: detail);
    }

    // —— 3. 工具调用 ——
    private async IAsyncEnumerable<VerificationEvent> RunToolsAsync(IChatClient client, [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string status, detail;
        string? evidence = null;

        try
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "北京的天气怎么样？请调用 get_weather 工具获取，不要凭空回答。")],
                new ChatOptions { MaxOutputTokens = 256, Tools = [WeatherTool] },
                ct);

            var call = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .FirstOrDefault();

            if (call is null)
            {
                status = "fail";
                detail = "模型未发起工具调用";
                evidence = Truncate(response.Text);
            }
            else
            {
                status = "pass";
                detail = $"调用了函数 {call.Name}（{sw.ElapsedMilliseconds}ms）";
                evidence = JsonSerializer.Serialize(call.Arguments, VerificationJson.Options);
            }
        }
        catch (OperationCanceledException)
        {
            status = "fail";
            detail = "超时(30s)";
        }
        catch (Exception ex)
        {
            status = "fail";
            detail = ex.Message;
        }

        yield return new VerificationEvent("result", Check: CheckTools, Status: status,
            ElapsedMs: sw.ElapsedMilliseconds, Detail: detail, Evidence: evidence);
    }

    // —— 4. JSON 模式 ——
    private async IAsyncEnumerable<VerificationEvent> RunJsonAsync(IChatClient client, [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string status, detail;
        string? evidence = null;

        try
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "只输出一个 JSON 对象：{\"ok\":true,\"value\":42}，不要输出其他内容。")],
                new ChatOptions { MaxOutputTokens = 256, ResponseFormat = ChatResponseFormat.Json },
                ct);

            var text = response.Text?.Trim() ?? "";
            try
            {
                using var doc = JsonDocument.Parse(text);
                status = "pass";
                detail = $"返回合法 JSON（{sw.ElapsedMilliseconds}ms）";
            }
            catch (JsonException)
            {
                status = "fail";
                detail = "返回内容不是合法 JSON";
            }
            evidence = Truncate(text);
        }
        catch (OperationCanceledException)
        {
            status = "fail";
            detail = "超时(30s)";
        }
        catch (Exception ex)
        {
            status = "fail";
            detail = ex.Message;
        }

        yield return new VerificationEvent("result", Check: CheckJson, Status: status,
            ElapsedMs: sw.ElapsedMilliseconds, Detail: detail, Evidence: evidence);
    }

    private static string GetWeather([Description("城市名称")] string city) => $"{city}：22°C，晴";

    private static string Truncate(string? value, int max = 160)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";
}
