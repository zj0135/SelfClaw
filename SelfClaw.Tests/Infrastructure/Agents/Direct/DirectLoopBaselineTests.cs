using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

// Pure SDK probes: never run this pipeline inside DirectAgentChatRuntime.
public sealed class DirectLoopBaselineTests
{
    [Fact]
    public void Function_invoking_client_defaults_match_the_documented_sdk_baseline()
    {
        using var client = new FunctionInvokingChatClient(new EndlessToolClient(), NullLoggerFactory.Instance);
        client.MaximumIterationsPerRequest.Should().Be(40);
        client.AllowConcurrentInvocation.Should().BeFalse();
        client.MaximumConsecutiveErrorsPerRequest.Should().Be(3);
    }

    [Fact]
    public async Task Sdk_128_iterations_issue_129_requests_and_execute_128_tools()
    {
        var provider = new EndlessToolClient();
        var executions = 0;
        var tool = AIFunctionFactory.Create(() => ++executions, "probe");
        using var client = new ChatClientBuilder(provider).UseFunctionInvocation(configure: options =>
        {
            options.MaximumIterationsPerRequest = 128;
            options.MaximumConsecutiveErrorsPerRequest = 0;
        }).Build();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [tool] })) { }
        provider.Requests.Should().Be(129);
        executions.Should().Be(128);
    }

    [Fact]
    public async Task Function_invoking_loop_processes_multiple_tool_calls_serially_by_default()
    {
        var observed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = AIFunctionFactory.Create(async () =>
        {
            observed.Enqueue("a-start");
            entered.SetResult();
            await release.Task;
            observed.Enqueue("a-end");
            return "a";
        }, "a");
        var b = AIFunctionFactory.Create(() => { observed.Enqueue("b"); return "b"; }, "b");
        using var client = new ChatClientBuilder(new EndlessToolClient(twoCalls: true))
            .UseFunctionInvocation(configure: options => options.MaximumIterationsPerRequest = 1).Build();
        async Task RunAsync()
        {
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [a, b] })) { }
        }
        var run = RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().Equal("a-start", "a-end", "b");
    }

    private sealed class EndlessToolClient(bool twoCalls = false) : IChatClient
    {
        internal int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, twoCalls
                ? [new FunctionCallContent($"a-{Requests}", "a", new Dictionary<string, object?>()),
                   new FunctionCallContent($"b-{Requests}", "b", new Dictionary<string, object?>())]
                : [new FunctionCallContent($"call-{Requests}", "probe", new Dictionary<string, object?>())])
            { MessageId = $"message-{Requests}", FinishReason = ChatFinishReason.ToolCalls };
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
