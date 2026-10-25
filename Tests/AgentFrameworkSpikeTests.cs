using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

namespace LuckyCat.Tests;

public class AgentFrameworkSpikeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Agent_executes_tool_then_returns_typed_output()
    {
        var client = new ScriptedClient(
            Chat("call-1", "get_ping", new() { ["value"] = "hello" }),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"answer\":\"hello\"}")));
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "spike",
            ChatOptions = new ChatOptions
            {
                Instructions = "Call get_ping, then return JSON.",
                Tools = [AIFunctionFactory.Create(GetPing, "get_ping", "Echo a value.")]
            }
        });

        var response = await agent.RunAsync<PingAnswer>("go", null, Json, null, default);

        Assert.Equal("hello", response.Result.Answer);
        Assert.Equal(2, client.Calls);
        var toolResult = Assert.Single(client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>());
        Assert.Equal("call-1", toolResult.CallId);
        Assert.Equal("hello", toolResult.Result?.ToString());
    }

    [Fact]
    public async Task Conditional_edge_routes_typed_message()
    {
        var echo = new EchoExecutor();
        var upper = new UpperExecutor();
        var lower = new LowerExecutor();
        var workflow = new WorkflowBuilder(echo)
            .AddEdge<string>(echo, upper, v => v == "hello")
            .AddEdge<string>(echo, lower, v => v != "hello")
            .WithOutputFrom(upper, lower)
            .Build();

        var run = await InProcessExecution.RunAsync<string>(workflow, "hello");
        var output = Assert.Single(run.NewEvents.OfType<WorkflowOutputEvent>()
            .Select(e => e.Data).OfType<string>());

        Assert.Equal("HELLO", output);
    }

    [Description("Echo a value.")]
    private static string GetPing([Description("Value.")] string value) => value;

    private static ChatResponse Chat(string id, string name, Dictionary<string, object?> args) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(id, name, args)]));

    public sealed record PingAnswer(string Answer);

    private sealed class ScriptedClient(params ChatResponse[] responses) : IChatClient
    {
        private int _index;
        public int Calls { get; private set; }
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(messages.ToList());
            return Task.FromResult(responses[Math.Min(_index++, responses.Length - 1)]);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class EchoExecutor() : Executor<string, string>("echo")
    {
        public override ValueTask<string> HandleAsync(string input, IWorkflowContext context, CancellationToken ct) =>
            ValueTask.FromResult(input);
    }

    private sealed class UpperExecutor() : Executor<string, string>("upper")
    {
        public override ValueTask<string> HandleAsync(string input, IWorkflowContext context, CancellationToken ct) =>
            ValueTask.FromResult(input.ToUpperInvariant());
    }

    private sealed class LowerExecutor() : Executor<string, string>("lower")
    {
        public override ValueTask<string> HandleAsync(string input, IWorkflowContext context, CancellationToken ct) =>
            ValueTask.FromResult(input.ToLowerInvariant());
    }
}
