using LuckyCat.Contracts;
using Microsoft.Agents.AI;

namespace LuckyCat.Agents;

/// <summary>
/// 统一调用 <see cref="ChatClientAgent.RunAsync{T}(string, AgentSession?, JsonSerializerOptions?, ChatClientAgentRunOptions?, CancellationToken)"/>：
/// 使用 <see cref="AgentJson.Options"/> 反序列化结构化输出，并同时返回原始响应。
/// </summary>
public sealed class AgentRunner
{
    public async Task<(T Output, AgentResponse<T> Response)> RunAsync<T>(ChatClientAgent agent,
        string userPrompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(userPrompt);

        var response = await agent.RunAsync<T>(userPrompt, null, AgentJson.Options, null, ct);
        return (response.Result, response);
    }
}
