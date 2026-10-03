using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationDeletionServiceTests
{
    [Fact]
    public async Task Delete_cancels_preparation_then_waits_for_run_and_children_before_cascade()
    {
        using var context = new Context();
        var id = Guid.NewGuid();
        var handle = context.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)
            ?? throw new InvalidOperationException();
        var deletion = context.Service.DeleteAsync([id], false);
        handle.CancellationToken.IsCancellationRequested.Should().BeTrue();
        deletion.IsCompleted.Should().BeFalse();
        context.Children.Entered.Task.IsCompleted.Should().BeFalse();
        context.Repository.Deleted.Should().BeEmpty();
        context.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive).Should().BeNull();
        context.Runs.Complete(handle, false);
        await context.Children.Entered.Task;
        context.Repository.Deleted.Should().BeEmpty();
        context.Children.Release.SetResult();
        await deletion;
        context.Repository.Deleted.Should().Equal(id);
        context.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive).Should().BeNull("a deleted id must not recreate the conversation");
    }

    [Fact]
    public async Task Failed_child_drain_aborts_cascade_and_releases_only_the_owned_barrier()
    {
        using var context = new Context();
        var id = Guid.NewGuid();
        var deletion = context.Service.DeleteAsync([id], false);
        await context.Children.Entered.Task;
        context.Children.Release.SetException(new TimeoutException("child still running"));
        await FluentActions.Awaiting(() => deletion).Should().ThrowAsync<TimeoutException>();
        context.Repository.Deleted.Should().BeEmpty();
        var next = context.Runs.TryReserve(id, AgentExecutionMode.Cli, DirectTurnOrigin.Interactive)
            ?? throw new InvalidOperationException("Deletion barrier leaked.");
        context.Runs.Complete(next, false);
    }

    [Fact]
    public async Task Concurrent_delete_cannot_remove_the_first_deletions_barrier()
    {
        using var context = new Context();
        var id = Guid.NewGuid();
        var first = context.Service.DeleteAsync([id], false);
        await context.Children.Entered.Task;
        await FluentActions.Awaiting(() => context.Service.DeleteAsync([id], false)).Should().ThrowAsync<InvalidOperationException>();
        context.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive).Should().BeNull();
        context.Children.Release.SetResult();
        await first;
        context.Repository.Deleted.Should().Equal(id);
    }

    [Fact]
    public async Task A_failed_delete_releases_the_barrier_but_keeps_the_durable_queue_paused()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await fixture.CreateConversationAsync();
        await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation.Id,
            "queued", ConversationInputKind.FollowUp, null, "queued", fixture.Snapshot(), null, true));
        using var runs = new ConversationRunCoordinator(fixture.Inputs, NullLogger<ConversationRunCoordinator>.Instance);
        using var sessions = new ConversationSessionCoordinator(fixture.Conversations, fixture.Turns, runs, new Sink());
        var children = new Children();
        var service = new ConversationDeletionService(runs, sessions, children,
            new ConversationWorkspaceService(new EmptyRoots()), fixture.Conversations, null, fixture.Inputs);

        var deletion = service.DeleteAsync([conversation.Id], false);
        await children.Entered.Task;
        children.Release.SetException(new TimeoutException("child still running"));
        await FluentActions.Awaiting(() => deletion).Should().ThrowAsync<TimeoutException>();

        var state = await fixture.Inputs.GetQueueStateAsync(conversation.Id);
        state.Paused.Should().BeTrue("a failed delete must not silently resume the queue");
        state.Items.Should().ContainSingle().Which.Preview.Should().Be("queued");

        // The tombstone is released, so a fresh reservation is possible, but nothing is claimed while paused.
        var handle = runs.TryReserve(conversation.Id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)
            ?? throw new InvalidOperationException("Deletion barrier leaked.");
        runs.Complete(handle, false);
        (await fixture.Inputs.TryClaimNextFollowUpAsync(conversation.Id, Guid.NewGuid(), Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task A_successful_delete_removes_the_queue_and_never_reclaims()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await fixture.CreateConversationAsync();
        await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation.Id,
            "queued", ConversationInputKind.FollowUp, null, "queued", fixture.Snapshot(), null, true));
        using var runs = new ConversationRunCoordinator(fixture.Inputs, NullLogger<ConversationRunCoordinator>.Instance);
        using var sessions = new ConversationSessionCoordinator(fixture.Conversations, fixture.Turns, runs, new Sink());
        var service = new ConversationDeletionService(runs, sessions, new Children { AutoRelease = true },
            new ConversationWorkspaceService(new EmptyRoots()), fixture.Conversations, null, fixture.Inputs);

        await service.DeleteAsync([conversation.Id], false);

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_input_state;")).Should().Be(0L);
        runs.TryReserve(conversation.Id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive).Should().BeNull();
    }

    private sealed class EmptyRoots : IWorkspaceRootRepository
    {
        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);
        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);
        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class Context : IDisposable
    {
        private readonly ConversationSessionCoordinator _sessions;
        public Context()
        {
            Runs = new(new EmptyConversationInputRepository(), NullLogger<ConversationRunCoordinator>.Instance);
            _sessions = new(Repository, new RecordingConversationTurnRepository(), Runs, new Sink());
            Service = new(Runs, _sessions, Children, new ConversationWorkspaceService(Repository), Repository);
        }
        public Repository Repository { get; } = new();
        public Children Children { get; } = new();
        public ConversationRunCoordinator Runs { get; }
        public ConversationDeletionService Service { get; }
        public void Dispose() { _sessions.Dispose(); Runs.Dispose(); }
    }

    private sealed class Children : ISubagentConversationLifecycle
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AutoRelease { get; init; }
        public Task CancelAndWaitAsync(Guid parentConversationId, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (AutoRelease) Release.TrySetResult();
            return Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class Sink : ITranscriptChangeSink
    {
        public void RequestStreamingPublish(bool autoScroll) { }
        public void PublishNow(bool autoScroll) { }
    }

    private sealed class Repository : IConversationRepository, IWorkspaceRootRepository
    {
        public List<Guid> Deleted { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConversationRecord>>([]);
        public Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<ConversationRecord?>(null);
        public Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default) => Task.FromResult(conversation);
        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) { Deleted.Add(conversationId); return Task.CompletedTask; }
        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MessageRecord>>([]);
        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);
        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);
        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default) => Task.FromResult(workspaceRoot);
        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
