using LuckyCat.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LuckyCat.Agents;

/// <summary>
/// 将 <see cref="IChatClient"/> 包装为带角色、工具与上下文提供器的 <see cref="ChatClientAgent"/>。
/// MAF 1.26 的 <c>ChatClientAgentOptions</c> 没有 <c>Instructions</c> 属性，指令必须写入
/// <see cref="ChatOptions.Instructions"/>。
/// </summary>
public sealed class TradingAgentFactory
{
    public const int MaxOutputTokens = 16384;

    public ChatClientAgent Create(IChatClient client, AgentRole role, StrategyConfig config,
        IList<AITool> tools, IList<AIContextProvider>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tools);

        return new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = role.ToString(),
            ChatOptions = new ChatOptions
            {
                Instructions = PromptLibrary.Build(role, config),
                Tools = tools,
                MaxOutputTokens = MaxOutputTokens
            },
            AIContextProviders = providers ?? []
        });
    }
}
