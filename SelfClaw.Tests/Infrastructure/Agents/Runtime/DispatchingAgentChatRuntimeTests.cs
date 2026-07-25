using System.Runtime.CompilerServices;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;

namespace SelfClaw.Tests.Infrastructure.Agents.Runtime;

public sealed class DispatchingAgentChatRuntimeTests
{
    [Fact]
    public async Task StreamTurnAsync_emits_exactly_one_terminal_event_as_the_last_item()
    {
        var adapter = new ScriptedAdapter(
            AgentExecutionMode.Direct,
            new AssistantTextDeltaEvent("block", "partial"),
            new RunCompletedEvent(RunCompletionStatus.Succeeded, "partial"),
            new AssistantTextDeltaEvent("late", "discarded"),
            new RunCompletedEvent(RunCompletionStatus.Failed, null, "discarded"));
        var runtime = CreateRuntime(adapter);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Direct)));

        events.Should().Equal(
            new AssistantTextDeltaEvent("block", "partial"),
            new RunCompletedEvent(RunCompletionStatus.Succeeded, "partial"));
    }

    [Fact]
    public async Task StreamTurnAsync_synthesizes_failure_when_adapter_ends_without_terminal_event()
    {
        var runtime = CreateRuntime(new ScriptedAdapter(
            AgentExecutionMode.Direct,
            new AssistantTextDeltaEvent("block", "partial")));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Direct)));

        events.Last().Should().BeOfType<RunCompletedEvent>()
            .Which.Should().Match<RunCompletedEvent>(item =>
                item.Status == RunCompletionStatus.Failed &&
                item.FinalText == null &&
                item.ErrorMessage != null &&
                item.ErrorMessage.Contains("without a completion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamTurnAsync_converts_non_cancellation_exception_before_terminal_to_failure()
    {
        var runtime = CreateRuntime(new ThrowingAdapter(
            AgentExecutionMode.Direct,
            new InvalidOperationException("adapter failed")));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Direct)));

        events.Should().ContainSingle().Which.Should().Be(
            new RunCompletedEvent(RunCompletionStatus.Failed, null, "adapter failed"));
    }

    [Fact]
    public async Task StreamTurnAsync_preserves_locked_terminal_when_adapter_throws_after_it()
    {
        var runtime = CreateRuntime(new TerminalThenThrowingAdapter());

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Direct)));

        events.Should().ContainSingle().Which.Should().Be(
            new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
    }

    [Fact]
    public async Task StreamTurnAsync_disposes_adapter_before_delivering_terminal_event()
    {
        var adapter = new DisposalTrackingAdapter();
        var runtime = CreateRuntime(adapter);

        await foreach (var streamEvent in runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Direct)))
        {
            streamEvent.Should().BeOfType<RunCompletedEvent>();
            adapter.WasDisposed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task StreamTurnAsync_does_not_rewrite_locked_terminal_when_cancellation_arrives_after_cutoff()
    {
        var adapter = new CutoffAdapter();
        var runtime = CreateRuntime(adapter);
        using var cancellation = new CancellationTokenSource();
        var collecting = CollectAsync(runtime.StreamTurnAsync(
            CreateRequest(AgentExecutionMode.Direct),
            cancellation.Token));

        await adapter.WaitForCutoffAsync();
        cancellation.Cancel();
        adapter.Release();
        var events = await collecting;

        events.Should().ContainSingle().Which.Should().Be(
            new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
    }

    [Fact]
    public async Task StreamTurnAsync_rethrows_cancellation_before_terminal()
    {
        var runtime = CreateRuntime(new ThrowingAdapter(
            AgentExecutionMode.Direct,
            new OperationCanceledException()));

        var action = () => CollectAsync(runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Direct)));

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StreamTurnAsync_routes_each_mode_to_its_adapter()
    {
        var direct = new ScriptedAdapter(
            AgentExecutionMode.Direct,
            new RunCompletedEvent(RunCompletionStatus.Succeeded, "direct"));
        var cli = new ScriptedAdapter(
            AgentExecutionMode.Cli,
            new RunCompletedEvent(RunCompletionStatus.Succeeded, "cli"));
        var runtime = CreateRuntime(direct, cli);

        var directEvents = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Direct)));
        var cliEvents = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Cli)));

        directEvents.Last().Should().Be(new RunCompletedEvent(RunCompletionStatus.Succeeded, "direct"));
        cliEvents.Last().Should().Be(new RunCompletedEvent(RunCompletionStatus.Succeeded, "cli"));
        direct.CallCount.Should().Be(1);
        cli.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task StreamTurnAsync_cleans_up_adapter_when_consumer_abandons_the_stream()
    {
        var adapter = new BlockingAdapter();
        var runtime = CreateRuntime(adapter);

        await foreach (var streamEvent in runtime.StreamTurnAsync(CreateRequest(AgentExecutionMode.Direct)))
        {
            streamEvent.Should().BeOfType<RunStatusEvent>();
            break;
        }

        (await adapter.WaitForCancellationAsync()).Should().BeTrue();
        adapter.WasDisposed.Should().BeTrue();
    }

    private static DispatchingAgentChatRuntime CreateRuntime(params IAgentRuntimeAdapter[] adapters)
        => new(adapters);

    private static ChatTurnRequest CreateRequest(AgentExecutionMode mode)
    {
        var agent = new AgentRuntimeDefinition(
            "test", "Test", "test", mode,
            AgentRuntimeDefinition.SystemToolPolicy, [], [], [], "");
        return mode == AgentExecutionMode.Cli
            ? new CliChatTurnRequest(
                Guid.NewGuid(),
                WorkspaceRoot: null,
                agent,
                Messages: [],
                CliAgent: null,
                CliModel: null,
                CliReasoningEffort: null)
            : new DirectChatTurnRequest(
                Guid.NewGuid(),
                WorkspaceRoot: null,
                agent,
                Messages: [],
                ModelProfileId: null,
                ToolPermissionMode.RequireApproval,
                ToolApprovalHandler: null);
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> events)
    {
        var result = new List<AgentStreamEvent>();
        await foreach (var streamEvent in events)
        {
            result.Add(streamEvent);
        }

        return result;
    }

    private sealed class ScriptedAdapter : IAgentRuntimeAdapter
    {
        private readonly IReadOnlyList<AgentStreamEvent> _events;

        public ScriptedAdapter(AgentExecutionMode mode, params AgentStreamEvent[] events)
        {
            Mode = mode;
            _events = events;
        }

        public AgentExecutionMode Mode { get; }
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            foreach (var streamEvent in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return streamEvent;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ThrowingAdapter : IAgentRuntimeAdapter
    {
        private readonly Exception _exception;

        public ThrowingAdapter(AgentExecutionMode mode, Exception exception)
        {
            Mode = mode;
            _exception = exception;
        }

        public AgentExecutionMode Mode { get; }

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw _exception;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class BlockingAdapter : IAgentRuntimeAdapter
    {
        private readonly TaskCompletionSource<bool> _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentExecutionMode Mode => AgentExecutionMode.Direct;
        public bool WasDisposed { get; private set; }

        public Task<bool> WaitForCancellationAsync()
            => _cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return new RunStatusEvent(AgentRunStatus.Requesting);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                WasDisposed = true;
                _cancelled.TrySetResult(cancellationToken.IsCancellationRequested);
            }
        }
    }

    private sealed class TerminalThenThrowingAdapter : IAgentRuntimeAdapter
    {
        public AgentExecutionMode Mode => AgentExecutionMode.Direct;

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
            await Task.CompletedTask;
            throw new InvalidOperationException("late failure");
        }
    }

    private sealed class DisposalTrackingAdapter : IAgentRuntimeAdapter
    {
        public AgentExecutionMode Mode => AgentExecutionMode.Direct;
        public bool WasDisposed { get; private set; }

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
                await Task.CompletedTask;
            }
            finally
            {
                WasDisposed = true;
            }
        }
    }

    private sealed class CutoffAdapter : IAgentRuntimeAdapter
    {
        private readonly TaskCompletionSource _cutoff =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentExecutionMode Mode => AgentExecutionMode.Direct;

        public Task WaitForCutoffAsync() => _cutoff.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
            _cutoff.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }
}
