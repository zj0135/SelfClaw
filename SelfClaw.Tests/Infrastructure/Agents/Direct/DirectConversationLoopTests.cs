using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

public sealed class DirectConversationLoopTests
{
    [Fact]
    public async Task Request_budget_issues_128_requests_and_executes_only_127_tool_batches()
    {
        const int maximumIterations = 128;
        var provider = new EndlessToolChatClient("probe_tool");
        var executions = 0;
        var factory = CreateFactory(provider);
        var runtime = CreateRuntime(factory, new FixedCapabilityResolver(
            Lease(CreateProbeTool(() => executions++))));

        var terminal = await RunAsync(runtime, factory.Profile.Id);

        provider.Requests.Should().Be(maximumIterations);
        executions.Should().Be(maximumIterations - 1);
        terminal.Status.Should().Be(RunCompletionStatus.Failed);
        terminal.ErrorMessage.Should().Contain("BudgetExhausted");
    }

    [Fact]
    public async Task Explicit_loop_processes_multiple_tool_calls_serially()
    {
        var provider = new TwoToolCallChatClient();
        var factory = CreateFactory(provider);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var toolA = AIFunctionFactory.Create(async () =>
        {
            observed.Enqueue("a-start");
            entered.SetResult();
            await release.Task;
            observed.Enqueue("a-end");
            return "a";
        }, "probe_a");
        var toolB = CreateProbeTool("probe_b", () =>
        {
            observed.Enqueue("b");
            return "b";
        });
        var runtime = CreateRuntime(factory, new FixedCapabilityResolver(
            Lease(toolA, toolB)));

        var run = RunAsync(runtime, factory.Profile.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var terminal = await run.WaitAsync(TimeSpan.FromSeconds(5));

        observed.Should().Equal("a-start", "a-end", "b");
        terminal.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Incremental_usage_fragments_count_as_one_provider_call()
    {
        var provider = new MultiUsageChatClient();
        var factory = CreateFactory(provider);
        var runtime = CreateRuntime(factory, new FixedCapabilityResolver(Lease()));
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id);

        var events = new List<AgentStreamEvent>();
        await foreach (var item in runtime.StreamTurnAsync(request))
        {
            events.Add(item);
        }

        provider.Requests.Should().Be(1);
        var usage = events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
        usage.InputTokens.Should().Be(13);
        usage.OutputTokens.Should().Be(6);
        usage.ProviderCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_response_without_usage_reports_no_usage_event()
    {
        var provider = new NoUsageChatClient();
        var factory = CreateFactory(provider);
        var runtime = CreateRuntime(factory, new FixedCapabilityResolver(Lease()));
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id);

        var events = new List<AgentStreamEvent>();
        await foreach (var item in runtime.StreamTurnAsync(request))
        {
            events.Add(item);
        }

        events.OfType<UsageReportedEvent>().Should().BeEmpty("usage is never fabricated as zero");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status
            .Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Two_tool_round_trips_capture_history_events_terminal_and_usage()
    {
        var provider = new RoundTripChatClient();
        var factory = CreateFactory(provider);
        var executions = 0;
        var runtime = CreateRuntime(factory, new FixedCapabilityResolver(Lease(
            CreateProbeTool(() => new DirectToolResult(ToolCallStatus.Completed, $"result-{++executions}",
                JsonSerializer.SerializeToElement($"result-{executions}"), $"result-{executions}")))));
        var events = new List<AgentStreamEvent>();
        await foreach (var item in runtime.StreamTurnAsync(DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id)))
            events.Add(item);

        executions.Should().Be(2);
        provider.Histories.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
        {
            var contents = provider.Histories[i].SelectMany(message => message.Contents).ToArray();
            contents.OfType<FunctionCallContent>().Select(call => call.CallId)
                .Should().Equal(Enumerable.Range(1, i).Select(n => $"call-{n}"));
            contents.OfType<FunctionResultContent>().Select(result => result.CallId)
                .Should().Equal(Enumerable.Range(1, i).Select(n => $"call-{n}"));
            contents.OfType<FunctionResultContent>().Select(result => ((DirectToolResult)result.Result!).Summary)
                .Should().Equal(Enumerable.Range(1, i).Select(n => $"result-{n}"));
        }
        events.OfType<ToolCallStartedEvent>().Select(item => item.ToolCallId).Should().Equal("call-1", "call-2");
        events.OfType<ToolCallCompletedEvent>().Select(item => item.ToolCallId).Should().Equal("call-1", "call-2");
        events.OfType<ToolCallCompletedEvent>().Should().OnlyContain(item => item.Status == ToolCallStatus.Completed);
        events.OfType<AssistantThinkingDeltaEvent>().Should().HaveCount(3);
        events.OfType<RunStartedEvent>().Should().ContainSingle();
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Subject;
        terminal.Status.Should().Be(RunCompletionStatus.Succeeded);
        terminal.FinalText.Should().Be("round-1round-2done");
        var usage = events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
        usage.InputTokens.Should().Be(60);
        usage.OutputTokens.Should().Be(6);
        usage.ProviderCalls.Should().Be(3);
        for (var i = 1; i <= 2; i++)
            events.FindIndex(e => e is ToolCallStartedEvent started && started.ToolCallId == $"call-{i}")
                .Should().BeLessThan(events.FindIndex(e => e is ToolCallCompletedEvent ended && ended.ToolCallId == $"call-{i}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Logical_turn_delivers_one_lifecycle_and_one_hook_pair_per_executed_tool(int toolRounds)
    {
        var payloads = new System.Collections.Concurrent.ConcurrentQueue<JsonElement>();
        var log = new SelfClaw.Infrastructure.Agents.Direct.Hooks.PluginHookExecutionLog();
        var executor = new SelfClaw.Infrastructure.Agents.Direct.Hooks.AsyncHookExecutor(
            (_, payload, _, _) =>
            {
                payloads.Enqueue(JsonDocument.Parse(payload).RootElement.Clone());
                return Task.FromResult(Hooks.TestDoubles.HookTestFactory.Success(""));
            }, log, new SelfClaw.Infrastructure.Extensions.PluginVersionLeaseManager());
        await executor.StartAsync(CancellationToken.None);
        try
        {
            var provider = new RoundTripChatClient(toolRounds);
            var factory = CreateFactory(provider);
            var tool = CreateProbeTool(() => new DirectToolResult(ToolCallStatus.Completed, "ok",
                JsonSerializer.SerializeToElement("ok"), "ok"));
            var hooks = new[]
            {
                Hooks.TestDoubles.HookTestFactory.CreateHook("loop-audit", "start", PluginHookEvent.RunStarting,
                    command: "cmd.exe", arguments: ["/d", "/c", "more >nul & echo {}"]) with { PluginRoot = Path.GetTempPath() },
                Hooks.TestDoubles.HookTestFactory.CreateHook("loop-audit", "before", PluginHookEvent.ToolExecuting,
                    command: "cmd.exe", arguments: ["/d", "/c", "more >nul & echo {}"]) with { PluginRoot = Path.GetTempPath() },
                Hooks.TestDoubles.HookTestFactory.CreateHook("loop-audit", "after", PluginHookEvent.ToolExecuted, runAsync: true),
                Hooks.TestDoubles.HookTestFactory.CreateHook("loop-audit", "complete", PluginHookEvent.RunCompleted, runAsync: true)
            };
            var lease = new DirectTurnCapabilityLease([], [new DirectToolBinding(tool, new(tool.Name, ToolCallKind.Other))],
                new Dictionary<Guid, string>(), [], hooks: hooks);
            var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new FixedCapabilityResolver(lease),
                Hooks.TestDoubles.HookTestFactory.CreateFactory(executor, log));
            var terminal = await RunAsync(runtime, factory.Profile.Id);
            terminal.Status.Should().Be(RunCompletionStatus.Succeeded);
            await executor.StopAsync(CancellationToken.None);
            log.GetRecent("loop-audit").Where(entry => entry.Event == "runStarting").Should().ContainSingle()
                .Which.Outcome.Should().Be("continued");
            log.GetRecent("loop-audit").Where(entry => entry.Event == "toolExecuting").Should().HaveCount(toolRounds)
                .And.OnlyContain(entry => entry.Outcome == "continued");
            payloads.Count(payload => payload.GetProperty("event").GetString() == "toolExecuted").Should().Be(toolRounds);
            var completion = payloads.Where(payload => payload.GetProperty("event").GetString() == "runCompleted")
                .Should().ContainSingle().Subject;
            completion.GetProperty("status").GetString().Should().Be("succeeded");
            completion.GetProperty("toolCallCount").GetInt32().Should().Be(toolRounds);
            provider.Histories.Should().HaveCount(toolRounds + 1);
        }
        finally
        {
            await executor.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RoundTripChatClient(int toolRounds = 2) : IChatClient
    {
        internal List<ChatMessage[]> Histories { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Histories.Add(messages.ToArray());
            var request = Histories.Count;
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new TextReasoningContent($"think-{request}"), new TextContent(request == toolRounds + 1 ? "done" : $"round-{request}"),
                 new UsageContent(new UsageDetails { InputTokenCount = request * 10, OutputTokenCount = 2 })])
            { MessageId = $"message-{request}" };
            yield return new ChatResponseUpdate(ChatRole.Assistant, request <= toolRounds
                ? [new FunctionCallContent($"call-{request}", "probe_tool", new Dictionary<string, object?>())] : [])
            { MessageId = $"message-{request}", FinishReason = request <= toolRounds ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop };
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<RunCompletedEvent> RunAsync(DirectAgentChatRuntime runtime, Guid modelProfileId)
    {
        RunCompletedEvent? terminal = null;
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(modelProfileId);
        await foreach (var item in runtime.StreamTurnAsync(request))
        {
            if (item is RunCompletedEvent completed)
            {
                terminal = completed;
            }
        }

        terminal.Should().NotBeNull();
        return terminal!;
    }

    private static DirectAgentChatRuntime CreateRuntime(
        DirectAgentChatRuntimeTests.FakeChatClientFactory factory,
        IDirectTurnCapabilityResolver resolver)
        => DirectAgentChatRuntimeTests.CreateRuntime(factory, resolver);

    private static DirectAgentChatRuntimeTests.FakeChatClientFactory CreateFactory(IChatClient native)
        => new(native) { UsageUpdateKind = SelfClaw.Infrastructure.AiProviders.Models.AiUsageUpdateKind.Incremental };

    private static DirectTurnCapabilityLease Lease(params AIFunction[] tools)
        => new(
            [],
            tools.Select(tool => new DirectToolBinding(tool, new DirectToolDescriptor(tool.Name, ToolCallKind.Other))).ToArray(),
            new Dictionary<Guid, string>(),
            []);

    private static AIFunction CreateProbeTool(Func<object> invoke)
        => AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions
        {
            Name = "probe_tool",
            Description = "Synthetic tool used to observe loop behavior.",
            ExcludeResultSchema = true,
            MarshalResult = (value, _, _) => ValueTask.FromResult(value)
        });

    private static AIFunction CreateProbeTool(string name, Func<object> invoke)
        => AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = "Synthetic tool used to observe loop behavior.",
            ExcludeResultSchema = true,
            MarshalResult = (value, _, _) => ValueTask.FromResult(value)
        });

    private sealed class FixedCapabilityResolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(
            DirectChatTurnRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    private sealed class MultiUsageChatClient : IChatClient
    {
        private int _requests;

        internal int Requests => _requests;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requests);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
            [
                new TextContent("answer"),
                new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 }),
                new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 4 })
            ])
            {
                MessageId = "answer-message",
                FinishReason = ChatFinishReason.Stop
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class NoUsageChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("answer")])
            {
                MessageId = "answer-message",
                FinishReason = ChatFinishReason.Stop
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class EndlessToolChatClient(string toolName) : IChatClient
    {
        private int _requests;

        internal int Requests => _requests;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requests);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent($"call-{request}", toolName, new Dictionary<string, object?>())])
            {
                MessageId = $"message-{request}",
                FinishReason = ChatFinishReason.ToolCalls
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class TwoToolCallChatClient : IChatClient
    {
        private int _requests;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-a", "probe_a", new Dictionary<string, object?>()),
                    new FunctionCallContent("call-b", "probe_b", new Dictionary<string, object?>())
                ])
                {
                    MessageId = "tool-message",
                    FinishReason = ChatFinishReason.ToolCalls
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
            {
                MessageId = "answer-message",
                FinishReason = ChatFinishReason.Stop
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
