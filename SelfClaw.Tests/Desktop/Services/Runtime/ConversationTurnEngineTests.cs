using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationTurnEngineTests
{
    [Fact]
    public async Task ExecuteAsync_starts_atomically_and_keeps_turn_message_and_tool_identities_distinct()
    {
        var runtime = new RecordingRuntime
        {
            Events = [new RunStartedEvent("session", "model", null), new AssistantTextDeltaEvent("a", "hello "),
                new ToolCallStartedEvent("call", "read_file", "{}", ToolCallKind.Read),
                new ToolCallCompletedEvent("call", ToolCallStatus.Completed, "read", "contents"),
                new AssistantTextDeltaEvent("a", "world"),
                new UsageReportedEvent(new TurnUsage(InputTokens: 11, OutputTokens: 7)),
                new RunCompletedEvent(RunCompletionStatus.Succeeded, "hello world")]
        };
        using var context = new Context(runtime);
        var result = await context.ExecuteAsync();
        result.Persisted.Should().BeTrue();
        var start = context.Turns.Starts.Should().ContainSingle().Which;
        start.Conversation.Title.Should().Be("prompt");
        var request = runtime.Requests.Should().ContainSingle().Which;
        var user = request.Messages.Should().ContainSingle().Which;
        user.Role.Should().Be(MessageRole.User);
        user.TurnId.Should().Be(request.TurnId);
        user.Id.Should().NotBe(request.TurnId);
        request.Turns.Should().ContainSingle().Which.Status.Should().Be(ConversationTurnStatus.Running);
        ((DirectChatTurnRequest)request).InputSession.Should().NotBeNull();
        var commit = context.Turns.Finalizations.Should().ContainSingle().Which;
        var assistant = commit.Messages.Should().ContainSingle().Which;
        assistant.Id.Should().NotBe(user.Id).And.NotBe(commit.Turn.Id);
        assistant.TurnId.Should().Be(commit.Turn.Id);
        assistant.Status.Should().Be(MessageStatus.Sealed);
        assistant.MarkdownContent.Should().Be("hello world");
        assistant.Sequence.Should().BeGreaterThan(user.Sequence);
        commit.ToolExecutions.Should().ContainSingle().Which.MessageId.Should().Be(assistant.Id);
        commit.Turn.Usage.Should().Be(new TurnUsage(InputTokens: 11, OutputTokens: 7, UncachedInputTokens: 11));
        context.Turns.Progress.Should().HaveCount(2);
        context.Notifier.Turns.Should().Equal(commit.Turn.Id);
        context.Runs.RunningConversationIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(RunCompletionStatus.Failed, ConversationTurnStatus.Failed)]
    [InlineData(RunCompletionStatus.Blocked, ConversationTurnStatus.Blocked)]
    public async Task No_output_terminal_persists_a_turn_without_an_assistant(RunCompletionStatus completion, ConversationTurnStatus status)
    {
        using var context = new Context(new RecordingRuntime { Events = [new RunCompletedEvent(completion, null, "provider reason")] });
        var result = await context.ExecuteAsync();
        result.Persisted.Should().BeTrue();
        result.Turn?.Status.Should().Be(status);
        context.Turns.Finalizations.Single().Messages.Should().BeEmpty();
        context.Sessions.SelectedMessages.Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        context.Sessions.SelectedTurns.Should().ContainSingle().Which.ErrorMessage.Should().Be("provider reason");
    }

    [Fact]
    public async Task User_stop_cancels_current_fragment_and_running_tools_then_rethrows_cancellation()
    {
        var runtime = new RecordingRuntime
        {
            Events = [new AssistantTextDeltaEvent("a", "partial"), new ToolCallStartedEvent("call", "run_shell_command", "{}", ToolCallKind.Run)],
            Failure = new OperationCanceledException()
        };
        using var context = new Context(runtime);
        runtime.BeforeFailure = () => context.Runs.Stop(context.ConversationId);
        await FluentActions.Awaiting(() => context.ExecuteAsync()).Should().ThrowAsync<OperationCanceledException>();
        var commit = context.Turns.Finalizations.Single();
        commit.Turn.Status.Should().Be(ConversationTurnStatus.Cancelled);
        commit.Messages.Single().MarkdownContent.Should().Contain("partial");
        commit.ToolExecutions.Single().Status.Should().Be(ToolExecutionStatus.Cancelled);
        context.Notifier.Turns.Should().BeEmpty();
        context.Runs.IsRunning(context.ConversationId).Should().BeFalse();
    }

    [Fact]
    public async Task Duplicate_terminal_does_not_persist_or_notify_twice()
    {
        using var context = new Context(new RecordingRuntime
        {
            Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"), new RunCompletedEvent(RunCompletionStatus.Failed, null, "late")]
        });
        await context.ExecuteAsync();
        context.Turns.Finalizations.Should().ContainSingle().Which.Turn.Status.Should().Be(ConversationTurnStatus.Succeeded);
        context.Notifier.Turns.Should().ContainSingle();
    }

    [Fact]
    public async Task Cli_uses_the_same_start_transaction_but_no_input_session()
    {
        var runtime = new RecordingRuntime { Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done")] };
        using var context = new Context(runtime);
        await context.ExecuteAsync(AgentExecutionMode.Cli);
        var request = runtime.Requests.Single().Should().BeOfType<CliChatTurnRequest>().Which;
        request.TurnId.Should().Be(context.Turns.Finalizations.Single().Turn.Id);
        request.Messages.Single().Id.Should().NotBe(request.TurnId);
    }

    [Fact]
    public async Task Preparing_history_is_registered_and_stop_prevents_the_start_transaction()
    {
        using var context = new Context(new RecordingRuntime());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Repository.ReadMessages = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return []; };
        var handle = context.Reserve();
        var execution = context.Engine.ExecuteAsync(handle, context.Request());
        await entered.Task;
        context.Runs.TryReserve(context.ConversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive).Should().BeNull();
        var stop = context.Runs.StopAndWaitAsync(context.ConversationId, TimeSpan.FromSeconds(5));
        await FluentActions.Awaiting(() => execution).Should().ThrowAsync<OperationCanceledException>();
        await stop;
        context.Turns.Starts.Should().BeEmpty();
        (await handle.Started).Should().BeNull();
        release.SetResult();
    }

    [Fact]
    public async Task Blocked_start_transaction_prevents_provider_and_does_not_block_other_conversations()
    {
        var runtime = new RecordingRuntime { Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done")] };
        using var context = new Context(runtime);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Turns.BeforeStart = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var handle = context.Reserve();
        var execution = context.Engine.ExecuteAsync(handle, context.Request());
        await entered.Task;
        runtime.Requests.Should().BeEmpty();
        handle.Started.IsCompleted.Should().BeFalse();
        var other = context.Runs.TryReserve(Guid.NewGuid(), AgentExecutionMode.Cli, DirectTurnOrigin.Interactive)
            ?? throw new InvalidOperationException();
        context.Runs.Complete(other, false);
        release.SetResult();
        (await execution).Persisted.Should().BeTrue();
    }

    [Fact]
    public async Task Engine_rejects_reusing_an_admitted_handle_without_releasing_the_running_owner()
    {
        var runtime = new BlockingRuntime();
        using var context = new Context(runtime);
        var handle = context.Reserve();
        var first = context.Engine.ExecuteAsync(handle, context.Request());
        await runtime.Requested.Task;
        await FluentActions.Awaiting(() => context.Engine.ExecuteAsync(handle, context.Request())).Should().ThrowAsync<InvalidOperationException>();
        context.Runs.GetActiveRun(context.ConversationId).Should().BeSameAs(handle);
        runtime.Release.SetResult();
        await first;
    }

    [Fact]
    public async Task Terminal_persistence_failure_returns_unpersisted_outcome_without_a_second_finalization_path()
    {
        using var context = new Context(new RecordingRuntime { Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done")] });
        context.Turns.FinalizationFailure = new IOException("disk full");
        var result = await context.ExecuteAsync();
        result.Persisted.Should().BeFalse();
        context.Turns.FinalizationAttempts.Should().Be(2, "only the finalizer's bounded retry runs");
        context.Notifier.Turns.Should().BeEmpty();
        context.Runs.RunningConversationIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Shutdown_waits_for_a_terminal_commit_before_releasing_the_owner()
    {
        using var context = new Context(new RecordingRuntime { Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done")] });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Turns.BeforeFinalize = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var handle = context.Reserve();
        var execution = context.Engine.ExecuteAsync(handle, context.Request());
        await entered.Task;
        var shutdown = context.Runs.StopAsync(CancellationToken.None);
        handle.CancellationToken.IsCancellationRequested.Should().BeTrue();
        handle.Completion.IsCompleted.Should().BeFalse();
        shutdown.IsCompleted.Should().BeFalse();
        context.Runs.GetActiveRun(context.ConversationId).Should().BeSameAs(handle);
        release.SetResult();
        (await execution).Persisted.Should().BeTrue();
        await shutdown;
        context.Turns.Finalizations.Should().ContainSingle();
    }

    [Fact]
    public async Task Terminal_persistence_cancellation_rethrows_and_releases_the_handle()
    {
        using var context = new Context(new RecordingRuntime { Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done")] });
        context.Turns.FinalizationFailure = new OperationCanceledException("write canceled");
        await FluentActions.Awaiting(() => context.ExecuteAsync()).Should().ThrowAsync<OperationCanceledException>();
        context.Notifier.Turns.Should().BeEmpty();
        context.Runs.RunningConversationIds.Should().BeEmpty();
    }

    private sealed class Context : IDisposable
    {
        private readonly AgentActivityCoordinator _activity;
        public Context(IAgentChatRuntime runtime)
        {
            Turns = new(Repository);
            Runs = new(Inputs, NullLogger<ConversationRunCoordinator>.Instance);
            Sessions = new(Repository, Turns, Runs, new SilentSink());
            var approval = new DesktopToolApprovalHandler();
            _activity = new(approval, NullLogger<AgentActivityCoordinator>.Instance);
            var root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
            var settings = new DesktopSettingsJsonStore(StoragePathDefaults.Create(root, Path.Combine(root, "test.db"), Path.Combine(root, "secrets")));
            Engine = new(Turns, new(Turns, NullLogger<DesktopTurnFinalizer>.Instance),
                new(Repository, Turns, Inputs, NullLogger<ConversationTurnRecorder>.Instance), runtime,
                Sessions, Runs, _activity, approval, ProgrammingSettingsTestFactory.Create(settings), Notifier,
                NullLogger<ConversationTurnEngine>.Instance);
        }
        public Guid ConversationId { get; } = Guid.NewGuid();
        public Repository Repository { get; } = new();
        public EmptyConversationInputRepository Inputs { get; } = new();
        public RecordingConversationTurnRepository Turns { get; }
        public ConversationRunCoordinator Runs { get; }
        public ConversationSessionCoordinator Sessions { get; }
        public ConversationTurnEngine Engine { get; }
        public Notifier Notifier { get; } = new();
        public ConversationRunHandle Reserve(AgentExecutionMode mode = AgentExecutionMode.Direct)
            => Runs.TryReserve(ConversationId, mode, DirectTurnOrigin.Interactive) ?? throw new InvalidOperationException();
        public DesktopConversationTurnRequest Request(AgentExecutionMode mode = AgentExecutionMode.Direct)
            => new(null, new("build", "Builder", "test", mode, AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], ""),
                "prompt", null, null, ToolPermissionMode.RequireApproval, ConversationId);
        public async Task<ConversationTurnExecutionResult> ExecuteAsync(AgentExecutionMode mode = AgentExecutionMode.Direct)
        {
            var handle = Reserve(mode);
            await Sessions.SelectAsync(ConversationId);
            return await Engine.ExecuteAsync(handle, Request(mode));
        }
        public void Dispose() { Runs.Dispose(); Sessions.Dispose(); _activity.Dispose(); }
    }

    private sealed class RecordingRuntime : IAgentChatRuntime
    {
        public IReadOnlyList<AgentStreamEvent> Events { get; init; } = [];
        public List<ChatTurnRequest> Requests { get; } = [];
        public Exception? Failure { get; init; }
        public Action? BeforeFailure { get; set; }
        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(ChatTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var item in Events) yield return item;
            BeforeFailure?.Invoke();
            if (Failure is not null) throw Failure;
            await Task.CompletedTask;
        }
    }

    private sealed class BlockingRuntime : IAgentChatRuntime
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(ChatTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requested.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
        }
    }

    private sealed class Notifier : IConversationCompletionNotifier
    {
        public List<Guid> Turns { get; } = [];
        public void Notify(ConversationRecord conversation, ConversationTurnRecord turn, IReadOnlyList<MessageRecord> messages) => Turns.Add(turn.Id);
    }

    private sealed class SilentSink : ITranscriptChangeSink
    {
        public void RequestStreamingPublish(bool autoScroll) { }
        public void PublishNow(bool autoScroll) { }
    }

    private sealed class Repository : IConversationRepository
    {
        public Func<Guid, CancellationToken, Task<IReadOnlyList<MessageRecord>>>? ReadMessages { get; set; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConversationRecord>>([]);
        public Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<ConversationRecord?>(null);
        public Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default) => Task.FromResult(conversation);
        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => ReadMessages?.Invoke(conversationId, cancellationToken) ?? Task.FromResult<IReadOnlyList<MessageRecord>>([]);
        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);
    }
}
