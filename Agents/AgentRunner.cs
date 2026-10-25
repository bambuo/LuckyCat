using System.Text.Json;
using LuckyCat.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LuckyCat.Agents;

/// <summary>
/// 一次结构化模型调用的结果：解析后的输出 + 原始消息/用量（供阶段统计落库）。
/// </summary>
public sealed record AgentRunResult<T>(T Output, IList<ChatMessage> Messages, UsageDetails? Usage, string Text);

/// <summary>模型输出无法按目标类型的 JSON 契约解析（阶段执行器映射为 stage_contract_violation）。</summary>
public sealed class AgentOutputFormatException(string typeName, Exception? inner)
    : Exception($"模型输出不符合 {typeName} 的 JSON 契约", inner);

/// <summary>
/// 统一调用 <see cref="AIAgent.RunAsync(string, AgentSession?, AgentRunOptions?, CancellationToken)"/>，
/// 并按 <see cref="AgentJson.Options"/> 把纯文本输出解析为目标类型。
/// <para>
/// 刻意不使用框架的 <c>RunAsync&lt;T&gt;</c>：它会强制 <c>response_format=json_schema</c>，
/// 而 DeepSeek 等提供商不支持该类型（实测返回 400 <c>invalid_request_error</c>
/// 「This response_format type is unavailable now」），会把每一次阶段调用直接打死。
/// 输出契约仍由系统提示词（块 7）约束，解析失败由阶段执行器按契约违规处理。
/// </para>
/// </summary>
public sealed class AgentRunner
{
    public async Task<AgentRunResult<T>> RunAsync<T>(ChatClientAgent agent, string userPrompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(userPrompt);

        var response = await agent.RunAsync(userPrompt, null, null, ct);
        var text = response.Text ?? string.Empty;
        var json = JsonPayload.Extract(text);

        T? output;
        try
        {
            output = JsonSerializer.Deserialize<T>(json, AgentJson.Options);
        }
        catch (JsonException ex)
        {
            throw new AgentOutputFormatException(typeof(T).Name, ex);
        }

        if (output is null) throw new AgentOutputFormatException(typeof(T).Name, null);
        return new AgentRunResult<T>(output, response.Messages, response.Usage, text);
    }
}

/// <summary>
/// 从模型回复中取出 JSON 载荷：容忍 ```json 围栏、前后说明文字与多余空白，
/// 按大括号配平（跳过字符串与转义）截取第一个完整对象。
/// </summary>
public static class JsonPayload
{
    public static string Extract(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return trimmed;

        var start = trimmed.IndexOf('{');
        if (start < 0) return trimmed;

        var end = FindObjectEnd(trimmed, start);
        return end < 0 ? trimmed[start..] : trimmed[start..(end + 1)];
    }

    private static int FindObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }
        return -1;
    }
}
