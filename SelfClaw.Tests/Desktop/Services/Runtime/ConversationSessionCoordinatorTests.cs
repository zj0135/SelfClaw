using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationSessionCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_load_is_evicted_so_preparation_can_retry(bool cancel)
    {
        using var context = new Context();
        var conversation = Conversation();
        var pending = context.Sessions.PrepareRuntimeStateAsync(conversation, false);
        if (cancel) context.Repository.Source(conversation.Id).SetCanceled();
        else context.Repository.Source(conversation.Id).SetException(new IOException("temporary"));
        if (cancel) await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
        else await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IOException>();
        context.Repository.Reset(conversation.Id);
        context.Repository.Source(conversation.Id).SetResult([Message(conversation.Id, "recovered")]);
        var state = await context.Sessions.PrepareRuntimeStateAsync(conversation, false);
        state.Messages.Single().MarkdownContent.Should().Be("recovered");
        context.Repository.Reads[conversation.Id].Should().Be(2);
    }

    [Fact]
    public async Task Cancelling_one_preparation_waiter_does_not_cancel_a_shared_selection_load()
    {
        using var context = new Context();
        var conversation = Conversation();
        var selection = context.Sessions.SelectAsync(conversation.Id);
        using var cancellation = new CancellationTokenSource();
        var preparation = context.Sessions.PrepareRuntimeStateAsync(conversation, false, cancellation.Token);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => preparation).Should().ThrowAsync<OperationCanceledException>();
        context.Repository.Source(conversation.Id).SetResult([Message(conversation.Id, "shared")]);
        await selection;
        context.Sessions.SelectedMessages.Single().MarkdownContent.Should().Be("shared");
        context.Repository.Reads[conversation.Id].Should().Be(1);
    }

    [Fact]
    public async Task Selection_clears_old_content_and_never_crosses_a_preparing_runs_identity()
    {
        using var context = new Context();
        var first = Conversation();
        var second = Conversation();
        context.Repository.Source(first.Id).SetResult([Message(first.Id, "first")]);
        await context.Sessions.SelectAsync(first.Id);
        var preparation = context.Sessions.PrepareRuntimeStateAsync(first, false);
        var selection = context.Sessions.SelectAsync(second.Id);
        context.Sessions.SelectedMessages.Should().BeEmpty();
        context.Repository.Source(second.Id).SetResult([Message(second.Id, "second")]);
        await selection;
        (await preparation).Messages.Single().MarkdownContent.Should().Be("first");
        context.Sessions.SelectedMessages.Single().MarkdownContent.Should().Be("second");
    }

    [Fact]
    public async Task Only_the_selected_completed_transcript_is_cached()
    {
        using var context = new Context();
        var conversations = Enumerable.Range(0, 12).Select(_ => Conversation()).ToArray();
        foreach (var conversation in conversations)
        {
            context.Repository.Source(conversation.Id).SetResult([Message(conversation.Id, "history")]);
            await context.Sessions.SelectAsync(conversation.Id);
        }
        await context.Sessions.PrepareRuntimeStateAsync(conversations[^1], false);
        context.Repository.Reads[conversations[^1].Id].Should().Be(1);
        await context.Sessions.PrepareRuntimeStateAsync(conversations[0], false);
        context.Repository.Reads[conversations[0].Id].Should().Be(2);
    }

    [Fact]
    public async Task Cancelling_a_preparing_reservation_does_not_invalidate_the_pending_selected_history()
    {
        using var context = new Context();
        var conversation = Conversation();
        var selection = context.Sessions.SelectAsync(conversation.Id);
        var handle = context.Reserve(conversation);
        context.Runs.Stop(conversation.Id);
        context.Runs.Complete(handle, false);
        context.Repository.Source(conversation.Id).SetResult([Message(conversation.Id, "history")]);
        await selection;
        context.Sessions.SelectedMessages.Single().MarkdownContent.Should().Be("history");
        context.Sessions.IsSelectedRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Preparing_reservation_is_busy_before_transcript_state_exists()
    {
        using var context = new Context();
        var conversation = Conversation();
        context.Repository.Source(conversation.Id).SetResult([]);
        await context.Sessions.SelectAsync(conversation.Id);
        var handle = context.Reserve(conversation);
        context.Sessions.IsSelectedRunning.Should().BeTrue();
        handle.RuntimeState.Should().BeNull();
        context.Runs.Complete(handle, false);
        context.Sessions.IsSelectedRunning.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detached_completion_either_keeps_committed_content_or_restores_initial_snapshot(bool committed)
    {
        using var context = new Context();
        var conversation = Conversation();
        context.Repository.Source(conversation.Id).SetResult([Message(conversation.Id, "existing")]);
        await context.Sessions.SelectAsync(conversation.Id);
        var handle = context.Reserve(conversation, true);
        var state = await context.Sessions.PrepareRuntimeStateAsync(conversation, true);
        context.Runs.AttachState(handle, state);
        state.ReplaceMessage(Message(conversation.Id, "provisional"));
        context.Sink.Immediate.Clear();
        state.RaiseTranscriptChanged(false);
        state.RaiseTranscriptChanged(true);
        context.Sessions.SelectedMessages.Select(message => message.MarkdownContent).Should().Equal("existing", "provisional");
        context.Sessions.IsSelectedContinuation.Should().BeTrue();
        context.Sink.Streaming.Should().Equal(true);
        context.Sink.Immediate.Should().Equal(true);
        await context.Sessions.SelectAsync(conversation.Id);
        context.Repository.Reads[conversation.Id].Should().Be(1);
        context.Runs.Complete(handle, committed);
        context.Sessions.SelectedMessages.Select(message => message.MarkdownContent)
            .Should().Equal(committed ? ["existing", "provisional"] : ["existing"]);
        context.Sessions.IsSelectedRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Offscreen_run_does_not_publish_or_replace_the_selected_snapshot()
    {
        using var context = new Context();
        var selected = Conversation();
        var other = Conversation();
        context.Repository.Source(selected.Id).SetResult([]);
        context.Repository.Source(other.Id).SetResult([]);
        await context.Sessions.SelectAsync(selected.Id);
        context.Sink.Immediate.Clear();
        var handle = context.Reserve(other, true);
        var state = await context.Sessions.PrepareRuntimeStateAsync(other, true);
        context.Runs.AttachState(handle, state);
        state.ReplaceMessage(Message(other.Id, "provisional"));
        state.RaiseTranscriptChanged(true);
        context.Runs.Complete(handle, false);
        context.Sessions.SelectedMessages.Should().BeEmpty();
        context.Sink.Immediate.Should().BeEmpty();
    }

    [Fact]
    public void Turn_only_changes_invalidate_content_snapshots_without_creating_an_assistant()
    {
        var conversation = Conversation();
        var turn = new ConversationTurnRecord(Guid.NewGuid(), conversation.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, DateTimeOffset.UtcNow);
        var state = new ConversationRuntimeState(conversation, [turn], [], []);
        var before = state.CaptureSnapshot();
        state.ReplaceTurn(turn with { Status = ConversationTurnStatus.Failed, ErrorMessage = "failed" });
        var after = state.CaptureSnapshot();
        after.Should().NotBeSameAs(before);
        after.Messages.Should().BeEmpty();
        after.Turns.Single().Status.Should().Be(ConversationTurnStatus.Failed);
    }

    private static ConversationRecord Conversation()
        => new(Guid.NewGuid(), "Conversation", null, ToolPermissionMode.RequireApproval, "build", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static long _sequence;
    private static MessageRecord Message(Guid conversationId, string text)
        => new(Guid.NewGuid(), conversationId, Guid.NewGuid(), Interlocked.Increment(ref _sequence), MessageRole.User,
            text, MessageStatus.Sealed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Context : IDisposable
    {
        public Context()
        {
            Runs = new(new EmptyConversationInputRepository(), NullLogger<ConversationRunCoordinator>.Instance);
            Sessions = new(Repository, new RecordingConversationTurnRepository(), Runs, Sink);
        }
        public ControlledRepository Repository { get; } = new();
        public Sink Sink { get; } = new();
        public ConversationRunCoordinator Runs { get; }
        public ConversationSessionCoordinator Sessions { get; }
        public ConversationRunHandle Reserve(ConversationRecord conversation, bool detached = false)
            => Runs.TryReserve(conversation.Id, AgentExecutionMode.Direct, detached ? DirectTurnOrigin.Continuation : DirectTurnOrigin.Interactive)
                ?? throw new InvalidOperationException();
        public void Dispose() { Sessions.Dispose(); Runs.Dispose(); }
    }

    private sealed class Sink : ITranscriptChangeSink
    {
        public List<bool> Immediate { get; } = [];
        public List<bool> Streaming { get; } = [];
        public void PublishNow(bool autoScroll) => Immediate.Add(autoScroll);
        public void RequestStreamingPublish(bool autoScroll) => Streaming.Add(autoScroll);
    }

    private sealed class ControlledRepository : IConversationRepository
    {
        private readonly Dictionary<Guid, TaskCompletionSource<IReadOnlyList<MessageRecord>>> _sources = [];
        public Dictionary<Guid, int> Reads { get; } = [];
        public TaskCompletionSource<IReadOnlyList<MessageRecord>> Source(Guid id)
        {
            if (!_sources.TryGetValue(id, out var source)) _sources.Add(id, source = new(TaskCreationOptions.RunContinuationsAsynchronously));
            return source;
        }
        public void Reset(Guid id) => _sources.Remove(id);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConversationRecord>>([]);
        public Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<ConversationRecord?>(null);
        public Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default) => Task.FromResult(conversation);
        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            Reads[conversationId] = Reads.GetValueOrDefault(conversationId) + 1;
            return Source(conversationId).Task.WaitAsync(cancellationToken);
        }
        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);
    }
}
