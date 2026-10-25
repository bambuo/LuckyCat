using System.ComponentModel;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace LuckyCat.Tests;

public class AgentRunnerTests
{
    public sealed record PingAnswer(string Answer);

    [Fact]
    public async Task Runner_returns_typed_output_and_response()
    {
        var client = new CapturingClient("{\"answer\":\"hello\",\"extra\":true}");
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "test",
            ChatOptions = new ChatOptions { Instructions = "Return JSON." }
        });
        var runner = new AgentRunner();

        var (output, response) = await runner.RunAsync<PingAnswer>(agent, "go", default);

        Assert.Equal("hello", output.Answer);
        Assert.Equal("hello", response.Result.Answer);
        Assert.Contains("go", Assert.Single(client.Requests)[^1].Text);
    }

    [Fact]
    public async Task Runner_forwards_cancellation_token()
    {
        var client = new CapturingClient("{\"answer\":\"ok\"}");
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { Name = "test" });
        var runner = new AgentRunner();
        using var cts = new CancellationTokenSource();

        await runner.RunAsync<PingAnswer>(agent, "go", cts.Token);

        Assert.Equal(cts.Token, Assert.Single(client.Tokens));
    }

    [Fact]
    public async Task Factory_passes_instructions_and_tools_to_chat_options()
    {
        var config = new StrategyConfig();
        var client = new CapturingClient("{\"answer\":\"ok\"}");
        var tool = AIFunctionFactory.Create(Echo, "get_ping", "Echo a value.");
        var factory = new TradingAgentFactory();

        var agent = factory.Create(client, AgentRole.Trader, config, [tool]);
        var response = await agent.RunAsync<PingAnswer>("go", null, AgentJson.Options, null, default);

        Assert.Equal("Trader", agent.Name);
        Assert.Equal("ok", response.Result.Answer);
        var options = Assert.Single(client.Options);
        Assert.NotNull(options);
        Assert.Equal(PromptLibrary.Build(AgentRole.Trader, config), options.Instructions);
        Assert.Contains(options.Tools!, t => t.Name == "get_ping");
        Assert.Equal(16384, options.MaxOutputTokens);
    }

    [Fact]
    public async Task Factory_passes_context_providers()
    {
        var config = new StrategyConfig();
        var client = new CapturingClient("{\"answer\":\"ok\"}");
        var provider = new NullProvider();
        var factory = new TradingAgentFactory();

        var agent = factory.Create(client, AgentRole.Analyst, config, [], [provider]);
        await agent.RunAsync<PingAnswer>("go", null, AgentJson.Options, null, default);

        Assert.NotNull(agent.AIContextProviders);
        Assert.Same(provider, Assert.Single(agent.AIContextProviders));
    }

    [Fact]
    public void Factory_without_providers_creates_empty_provider_list()
    {
        var factory = new TradingAgentFactory();

        var agent = factory.Create(new CapturingClient("{}"), AgentRole.Risk, new StrategyConfig(), []);

        Assert.NotNull(agent.AIContextProviders);
        Assert.Empty(agent.AIContextProviders);
    }

    [Description("Echo a value.")]
    private static string Echo([Description("Value.")] string value) => value;

    private sealed class NullProvider : AIContextProvider
    {
    }

    private sealed class CapturingClient(params string[] payloads) : IChatClient
    {
        private int _index;
        public List<ChatOptions?> Options { get; } = [];
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            Options.Add(options);
            Tokens.Add(cancellationToken);
            var payload = payloads[Math.Min(_index++, payloads.Length - 1)];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, payload)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
