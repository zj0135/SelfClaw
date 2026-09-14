using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Transcript.Abstractions;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationSessionCoordinatorTests
{
    [Fact]
    public async Task Shutdown_cancels_then_waits_for_terminal_state_and_release()
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository();
        repository.CompleteMessages(conversation.Id, []);
        repository.CompleteToolRuns(conversation.Id, []);
        using var coordinator = CreateCoordinator(repository);
        await coordinator.SelectAsync(conversation.Id);
        var state = await coordinator.StartTurnAsync(conversation);
        var shutdown = coordinator.StopAsync(CancellationToken.None);
        state.CancellationTokenSource.IsCancellationRequested.Should().BeTrue();
        shutdown.IsCompleted.Should().BeFalse();
        state.ReplaceMessage(CreateMessage(conversation.Id, "terminal committed"));
        coordinator.CompleteTurn(state);
        await shutdown;
        coordinator.IsRunning(conversation.Id).Should().BeFalse();
        coordinator.SelectedMessages.Should().ContainSingle().Which.MarkdownContent.Should().Be("terminal committed");
        await FluentActions.Awaiting(() => coordinator.StartTurnAsync(conversation)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_load_is_evicted_so_start_can_retry(bool cancelLoad)
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository();
        using var coordinator = CreateCoordinator(repository);
        var first = coordinator.StartTurnAsync(conversation);
        if (cancelLoad) repository.CancelMessages(conversation.Id);
        else repository.FailMessages(conversation.Id, new IOException("temporary read failure"));
        repository.CompleteToolRuns(conversation.Id, []);
        Func<Task> failure = () => first;
        if (cancelLoad) await failure.Should().ThrowAsync<OperationCanceledException>();
        else await failure.Should().ThrowAsync<IOException>();
        repository.ResetMessages(conversation.Id);
        repository.CompleteMessages(conversation.Id, [CreateMessage(conversation.Id, "recovered")]);

        var state = await coordinator.StartTurnAsync(conversation);

        state.Messages.Should().ContainSingle().Which.MarkdownContent.Should().Be("recovered");
        repository.MessageReads[conversation.Id].Should().Be(2);
        coordinator.AbandonTurn(state);
    }

    [Fact]
    public async Task Cancelling_a_waiter_does_not_cancel_the_shared_load()
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository();
        using var coordinator = CreateCoordinator(repository);
        using var cancellation = new CancellationTokenSource();
        var cancelled = coordinator.StartTurnAsync(conversation, cancellation.Token);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => cancelled).Should().ThrowAsync<OperationCanceledException>();
        var retry = coordinator.StartTurnAsync(conversation);
        repository.CompleteMessages(conversation.Id, [CreateMessage(conversation.Id, "shared")]);
        repository.CompleteToolRuns(conversation.Id, []);

        var state = await retry;

        state.Messages.Should().ContainSingle().Which.MarkdownContent.Should().Be("shared");
        repository.MessageReads[conversation.Id].Should().Be(1);
        coordinator.AbandonTurn(state);
    }

    [Fact]
    public async Task Only_the_selected_completed_transcript_is_retained()
    {
        var repository = new ControlledConversationRepository();
        using var coordinator = CreateCoordinator(repository);
        var conversations = Enumerable.Range(0, 40).Select(_ => CreateConversation(Guid.NewGuid())).ToArray();
        foreach (var conversation in conversations)
        {
            repository.CompleteMessages(conversation.Id, [CreateMessage(conversation.Id, "history")]);
            repository.CompleteToolRuns(conversation.Id, []);
            await coordinator.SelectAsync(conversation.Id);
        }

        var selected = await coordinator.StartTurnAsync(conversations[^1]);
        repository.MessageReads[conversations[^1].Id].Should().Be(1);
        coordinator.AbandonTurn(selected);
        var old = await coordinator.StartTurnAsync(conversations[0]);
        repository.MessageReads[conversations[0].Id].Should().Be(2, "completed off-screen loads must not become a permanent content cache");
        coordinator.AbandonTurn(old);
    }

    [Fact]
    public async Task SelectAsync_clears_the_previous_transcript_while_the_next_conversation_loads()
    {
        var firstConversationId = Guid.NewGuid();
        var secondConversationId = Guid.NewGuid();
        var repository = new ControlledConversationRepository();
        repository.CompleteMessages(firstConversationId, [CreateMessage(firstConversationId, "first")]);
        repository.CompleteToolRuns(firstConversationId, []);
        var coordinator = CreateCoordinator(repository);

        await coordinator.SelectAsync(firstConversationId);
        var secondSelection = coordinator.SelectAsync(secondConversationId);

        coordinator.SelectedMessages.Should().BeEmpty();

        repository.CompleteMessages(secondConversationId, [CreateMessage(secondConversationId, "second")]);
        repository.CompleteToolRuns(secondConversationId, []);
        await secondSelection;

        coordinator.SelectedMessages.Should().ContainSingle()
            .Which.MarkdownContent.Should().Be("second");
    }

    [Fact]
    public async Task SelectAsync_requests_bottom_alignment_for_the_selected_conversation()
    {
        var conversationId = Guid.NewGuid();
        var repository = new ControlledConversationRepository();
        repository.CompleteMessages(conversationId, [CreateMessage(conversationId, "history")]);
        repository.CompleteToolRuns(conversationId, []);
        var sink = new RecordingTranscriptChangeSink();
        using var coordinator = new ConversationSessionCoordinator(repository, sink);

        await coordinator.SelectAsync(conversationId);

        sink.ImmediatePublishes.Should().Equal(true, true);
    }

    [Fact]
    public async Task StartTurnAsync_uses_only_the_selected_conversations_loaded_history()
    {
        var firstConversationId = Guid.NewGuid();
        var secondConversationId = Guid.NewGuid();
        var repository = new ControlledConversationRepository();
        repository.CompleteMessages(firstConversationId, [CreateMessage(firstConversationId, "first")]);
        repository.CompleteToolRuns(firstConversationId, []);
        repository.CompleteMessages(secondConversationId, [CreateMessage(secondConversationId, "second")]);
        repository.CompleteToolRuns(secondConversationId, []);
        var coordinator = CreateCoordinator(repository);

        await coordinator.SelectAsync(firstConversationId);
        await coordinator.SelectAsync(secondConversationId);
        var state = await coordinator.StartTurnAsync(CreateConversation(secondConversationId));

        state.Messages.Should().ContainSingle()
            .Which.MarkdownContent.Should().Be("second");
        state.Messages.Should().OnlyContain(message => message.ConversationId == secondConversationId);
    }

    [Fact]
    public async Task StartTurnAsync_rejects_a_second_running_turn_for_the_same_conversation()
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository();
        repository.CompleteMessages(conversation.Id, []);
        repository.CompleteToolRuns(conversation.Id, []);
        var coordinator = CreateCoordinator(repository);
        await coordinator.SelectAsync(conversation.Id);
        await coordinator.StartTurnAsync(conversation);

        Func<Task> action = () => coordinator.StartTurnAsync(conversation);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartTurnAsync_keeps_the_submission_conversation_when_selection_changes_during_load()
    {
        var submittedConversationId = Guid.NewGuid();
        var selectedConversationId = Guid.NewGuid();
        var repository = new ControlledConversationRepository();
        var coordinator = CreateCoordinator(repository);

        var submittedSelection = coordinator.SelectAsync(submittedConversationId);
        var startTurn = coordinator.StartTurnAsync(CreateConversation(submittedConversationId));
        var nextSelection = coordinator.SelectAsync(selectedConversationId);

        repository.CompleteMessages(
            submittedConversationId,
            [CreateMessage(submittedConversationId, "submitted")]);
        repository.CompleteToolRuns(submittedConversationId, []);
        repository.CompleteMessages(
            selectedConversationId,
            [CreateMessage(selectedConversationId, "selected")]);
        repository.CompleteToolRuns(selectedConversationId, []);

        await Task.WhenAll(submittedSelection, nextSelection);
        var state = await startTurn;

        state.ConversationId.Should().Be(submittedConversationId);
        state.Messages.Should().ContainSingle()
            .Which.MarkdownContent.Should().Be("submitted");
        coordinator.IsSelected(selectedConversationId).Should().BeTrue();
        coordinator.SelectedMessages.Should().ContainSingle()
            .Which.MarkdownContent.Should().Be("selected");
    }

    [Fact]
    public async Task StartTurnAsync_propagates_a_selected_transcript_load_failure()
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository();
        var coordinator = CreateCoordinator(repository);
        var selection = coordinator.SelectAsync(conversation.Id);
        var startTurn = coordinator.StartTurnAsync(conversation);

        repository.FailMessages(conversation.Id, new InvalidOperationException("load failed"));
        repository.CompleteToolRuns(conversation.Id, []);

        await FluentActions.Awaiting(() => selection)
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("load failed");
        await FluentActions.Awaiting(() => startTurn)
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("load failed");
        coordinator.IsRunning(conversation.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Selected_runtime_changes_publish_directly_through_the_transcript_sink()
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository();
        repository.CompleteMessages(conversation.Id, []);
        repository.CompleteToolRuns(conversation.Id, []);
        var sink = new RecordingTranscriptChangeSink();
        var coordinator = new ConversationSessionCoordinator(repository, sink);

        await coordinator.SelectAsync(conversation.Id);
        var state = await coordinator.StartTurnAsync(conversation);
        sink.ImmediatePublishes.Clear();

        state.RaiseTranscriptChanged(false);
        state.RaiseTranscriptChanged(true);

        sink.StreamingPublishes.Should().Equal(true);
        sink.ImmediatePublishes.Should().Equal(true);
    }

    [Fact]
    public async Task Detached_turn_does_not_publish_or_replace_the_selected_transcript()
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository();
        repository.CompleteMessages(conversation.Id, [CreateMessage(conversation.Id, "existing")]);
        repository.CompleteToolRuns(conversation.Id, []);
        var sink = new RecordingTranscriptChangeSink();
        using var coordinator = new ConversationSessionCoordinator(repository, sink);

        await coordinator.SelectAsync(conversation.Id);
        var selectedBefore = coordinator.SelectedMessages.ToArray();
        sink.ImmediatePublishes.Clear();
        var detached = await coordinator.StartDetachedTurnAsync(conversation);
        detached.ReplaceMessage(CreateMessage(conversation.Id, "detached assistant"));
        detached.RaiseTranscriptChanged(immediate: true);

        coordinator.SelectedMessages.Should().Equal(selectedBefore);
        sink.StreamingPublishes.Should().BeEmpty();
        sink.ImmediatePublishes.Should().BeEmpty();
        coordinator.IsSelected(conversation.Id).Should().BeTrue();
        coordinator.AbandonTurn(detached);
    }

    private static ConversationRecord CreateConversation(Guid id)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationRecord(
            id,
            "Conversation",
            null,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
    }

    private static ConversationSessionCoordinator CreateCoordinator(IConversationRepository repository)
        => new(repository, new RecordingTranscriptChangeSink());

    private sealed class RecordingTranscriptChangeSink : ITranscriptChangeSink
    {
        public List<bool> StreamingPublishes { get; } = [];

        public List<bool> ImmediatePublishes { get; } = [];

        public void RequestStreamingPublish(bool autoScroll)
            => StreamingPublishes.Add(autoScroll);

        public void PublishNow(bool autoScroll)
            => ImmediatePublishes.Add(autoScroll);
    }

    private static MessageRecord CreateMessage(Guid conversationId, string content)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.User,
            content,
            MessageStatus.Completed,
            now,
            now);
    }

    private sealed class ControlledConversationRepository : IConversationRepository
    {
        private readonly Dictionary<Guid, TaskCompletionSource<IReadOnlyList<MessageRecord>>> _messages = [];
        private readonly Dictionary<Guid, TaskCompletionSource<IReadOnlyList<ToolExecutionRecord>>> _toolRuns = [];

        public Dictionary<Guid, int> MessageReads { get; } = [];

        public void ResetMessages(Guid conversationId) => _messages.Remove(conversationId);

        public void CancelMessages(Guid conversationId) => GetMessagesSource(conversationId).TrySetCanceled();

        public void CompleteMessages(Guid conversationId, IReadOnlyList<MessageRecord> messages)
            => GetMessagesSource(conversationId).TrySetResult(messages);

        public void FailMessages(Guid conversationId, Exception exception)
            => GetMessagesSource(conversationId).TrySetException(exception);

        public void CompleteToolRuns(Guid conversationId, IReadOnlyList<ToolExecutionRecord> toolRuns)
            => GetToolRunsSource(conversationId).TrySetResult(toolRuns);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<ConversationRecord?> GetConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationRecord?>(null);

        public Task<ConversationRecord> UpsertConversationAsync(
            ConversationRecord conversation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(conversation);

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
        {
            MessageReads[conversationId] = MessageReads.GetValueOrDefault(conversationId) + 1;
            return GetMessagesSource(conversationId).Task.WaitAsync(cancellationToken);
        }

        public Task<MessageRecord> UpsertMessageAsync(
            MessageRecord message,
            CancellationToken cancellationToken = default)
            => Task.FromResult(message);

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => GetToolRunsSource(conversationId).Task.WaitAsync(cancellationToken);

        public Task<ToolExecutionRecord> UpsertToolExecutionAsync(
            ToolExecutionRecord record,
            CancellationToken cancellationToken = default)
            => Task.FromResult(record);




        private TaskCompletionSource<IReadOnlyList<MessageRecord>> GetMessagesSource(Guid conversationId)
        {
            if (!_messages.TryGetValue(conversationId, out var source))
            {
                source = new TaskCompletionSource<IReadOnlyList<MessageRecord>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _messages[conversationId] = source;
            }

            return source;
        }

        private TaskCompletionSource<IReadOnlyList<ToolExecutionRecord>> GetToolRunsSource(Guid conversationId)
        {
            if (!_toolRuns.TryGetValue(conversationId, out var source))
            {
                source = new TaskCompletionSource<IReadOnlyList<ToolExecutionRecord>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _toolRuns[conversationId] = source;
            }

            return source;
        }
    }
}
