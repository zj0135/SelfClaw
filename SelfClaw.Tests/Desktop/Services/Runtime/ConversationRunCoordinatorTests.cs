using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationRunCoordinatorTests
{
    [Fact]
    public async Task Concurrent_reservations_have_exactly_one_owner_before_preparation()
    {
        using var runs = Create();
        var conversationId = Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return runs.TryReserve(conversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive);
        })).ToArray();
        start.SetResult();
        var handles = await Task.WhenAll(attempts);
        var owner = handles.OfType<ConversationRunHandle>().Should().ContainSingle().Which;
        owner.RuntimeState.Should().BeNull();
        runs.RunningConversationIds.Should().Equal(conversationId);
        runs.Complete(owner).Should().BeTrue();
        await owner.Completion;
    }

    [Fact]
    public async Task A_blocked_continuation_input_read_does_not_block_another_conversation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputs = new EmptyConversationInputRepository
        {
            HasUnprocessed = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return false; }
        };
        using var runs = Create(inputs);
        var first = Conversation();
        var pending = runs.TryReserveContinuationAsync(first);
        await entered.Task;
        runs.IsRunning(first.Id).Should().BeTrue();
        var other = runs.TryReserve(Guid.NewGuid(), AgentExecutionMode.Direct, DirectTurnOrigin.Interactive);
        other.Should().NotBeNull();
        pending.IsCompleted.Should().BeFalse();
        release.SetResult();
        var continuation = await pending;
        continuation.Should().NotBeNull();
        runs.Complete(continuation ?? throw new InvalidOperationException());
        runs.Complete(other ?? throw new InvalidOperationException());
    }

    [Fact]
    public async Task Unprocessed_user_inputs_prevent_a_continuation_and_release_its_reservation()
    {
        using var runs = Create(new() { HasUnprocessed = (_, _) => Task.FromResult(true) });
        var conversation = Conversation();
        (await runs.TryReserveContinuationAsync(conversation)).Should().BeNull();
        runs.IsRunning(conversation.Id).Should().BeFalse();
        var next = Reserve(runs, conversation.Id);
        runs.Complete(next);
    }

    [Fact]
    public async Task A_user_queue_blocks_only_its_own_continuation_and_not_another_parent()
    {
        var blocked = Conversation();
        var inputs = new EmptyConversationInputRepository { HasUnprocessed = (id, _) => Task.FromResult(id == blocked.Id) };
        using var runs = Create(inputs);

        (await runs.TryReserveContinuationAsync(blocked)).Should().BeNull();
        runs.IsRunning(blocked.Id).Should().BeFalse();

        // Another parent's continuation still wins the same admission gate.
        var other = Conversation();
        var continuation = await runs.TryReserveContinuationAsync(other);
        continuation.Should().NotBeNull();
        continuation!.Origin.Should().Be(DirectTurnOrigin.Continuation);
        runs.Complete(continuation);

        // Once the queue clears, the interactive path can own the previously blocked conversation.
        inputs.HasUnprocessed = (_, _) => Task.FromResult(false);
        runs.Complete(Reserve(runs, blocked.Id));
    }

    [Fact]
    public async Task Failed_preparation_releases_the_reservation_without_reporting_a_started_turn()
    {
        using var runs = Create(new() { HasUnprocessed = (_, _) => Task.FromException<bool>(new IOException("read failed")) });
        var conversation = Conversation();
        await FluentActions.Awaiting(() => runs.TryReserveContinuationAsync(conversation)).Should().ThrowAsync<IOException>();
        runs.IsRunning(conversation.Id).Should().BeFalse();
        var next = Reserve(runs, conversation.Id);
        runs.Complete(next, false);
        (await next.Started).Should().BeNull();
    }

    [Fact]
    public async Task Stop_and_shutdown_include_preparing_reservations_and_wait_for_owner_cleanup()
    {
        using var runs = Create();
        var handle = Reserve(runs);
        var stop = runs.StopAndWaitAsync(handle.ConversationId, TimeSpan.FromSeconds(5));
        handle.CancellationToken.IsCancellationRequested.Should().BeTrue();
        stop.IsCompleted.Should().BeFalse();
        var shutdown = runs.StopAsync(CancellationToken.None);
        runs.TryReserve(Guid.NewGuid(), AgentExecutionMode.Cli, DirectTurnOrigin.Interactive).Should().BeNull();
        shutdown.IsCompleted.Should().BeFalse();
        runs.Complete(handle, false);
        await Task.WhenAll(stop, shutdown);
    }

    [Fact]
    public async Task Cancellation_while_reading_continuation_priority_releases_preparing_owner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runs = Create(new()
        {
            HasUnprocessed = (_, token) => { entered.SetResult(); return never.Task.WaitAsync(token); }
        });
        var parent = Conversation();
        var preparing = runs.TryReserveContinuationAsync(parent);
        await entered.Task;
        runs.Stop(parent.Id);
        await FluentActions.Awaiting(() => preparing).Should().ThrowAsync<OperationCanceledException>();
        runs.IsRunning(parent.Id).Should().BeFalse();
    }

    [Fact]
    public void Late_completion_cannot_remove_a_successor_or_execute_an_old_handle()
    {
        using var runs = Create();
        var previous = Reserve(runs);
        runs.BeginExecution(previous);
        runs.Complete(previous).Should().BeTrue();
        var current = Reserve(runs, previous.ConversationId);
        runs.Complete(previous).Should().BeFalse();
        runs.GetActiveRun(current.ConversationId).Should().BeSameAs(current);
        FluentActions.Invoking(() => runs.BeginExecution(previous)).Should().Throw<InvalidOperationException>();
        runs.BeginExecution(current);
        FluentActions.Invoking(() => runs.BeginExecution(current)).Should().Throw<InvalidOperationException>();
        runs.Complete(current);
    }

    [Fact]
    public void Deletion_blocks_preparation_and_retains_successful_tombstones()
    {
        using var runs = Create();
        var id = Guid.NewGuid();
        var deleting = runs.BeginDeletion(id);
        runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive).Should().BeNull();
        FluentActions.Invoking(() => runs.BeginDeletion(id)).Should().Throw<InvalidOperationException>();
        runs.EndDeletion(new(id, deleting.Id), false);
        runs.TryReserve(id, AgentExecutionMode.Cli, DirectTurnOrigin.Interactive).Should().BeNull();
        runs.EndDeletion(deleting, true);
        runs.EndDeletion(deleting, false);
        runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive).Should().BeNull();
    }

    [Fact]
    public void Failed_deletion_only_releases_its_own_tombstone()
    {
        using var runs = Create();
        var id = Guid.NewGuid();
        var first = runs.BeginDeletion(id);
        runs.EndDeletion(first, false);
        var second = runs.BeginDeletion(id);
        runs.EndDeletion(first, false);
        runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive).Should().BeNull();
        runs.EndDeletion(second, false);
        runs.Complete(Reserve(runs, id));
    }

    [Theory]
    [InlineData(AgentExecutionMode.Direct, DirectTurnOrigin.Interactive, true)]
    [InlineData(AgentExecutionMode.Cli, DirectTurnOrigin.Interactive, false)]
    [InlineData(AgentExecutionMode.Direct, DirectTurnOrigin.Continuation, false)]
    public void Only_interactive_direct_handles_own_an_input_session(AgentExecutionMode mode, DirectTurnOrigin origin, bool hasInput)
    {
        using var runs = Create();
        var handle = runs.TryReserve(Guid.NewGuid(), mode, origin) ?? throw new InvalidOperationException();
        (handle.InputSession is not null).Should().Be(hasInput);
        runs.Complete(handle);
    }

    [Fact]
    public void Children_cannot_be_registered_with_the_interactive_owner()
    {
        using var runs = Create();
        FluentActions.Invoking(() => runs.TryReserve(Guid.NewGuid(), AgentExecutionMode.Direct, DirectTurnOrigin.Subagent))
            .Should().Throw<ArgumentException>();
        runs.RunningConversationIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observer_cancellation_propagates_without_leaking_admission_or_completion(bool duringCompletion)
    {
        using var runs = Create();
        ConversationRunHandle? captured = duringCompletion ? Reserve(runs) : null;
        runs.Changed += change =>
        {
            captured = change.Handle;
            throw new OperationCanceledException("observer cancelled");
        };
        if (duringCompletion)
            FluentActions.Invoking(() => runs.Complete(captured ?? throw new InvalidOperationException()))
                .Should().Throw<OperationCanceledException>();
        else
            FluentActions.Invoking(() => Reserve(runs)).Should().Throw<OperationCanceledException>();
        runs.RunningConversationIds.Should().BeEmpty();
        await (captured ?? throw new InvalidOperationException()).Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await runs.StopAsync(CancellationToken.None);
    }

    private static ConversationRunCoordinator Create(EmptyConversationInputRepository? inputs = null)
        => new(inputs ?? new(), NullLogger<ConversationRunCoordinator>.Instance);

    private static ConversationRunHandle Reserve(ConversationRunCoordinator runs, Guid? id = null)
        => runs.TryReserve(id ?? Guid.NewGuid(), AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)
            ?? throw new InvalidOperationException("Reservation rejected.");

    private static ConversationRecord Conversation()
        => new(Guid.NewGuid(), "Parent", null, ToolPermissionMode.RequireApproval, "build", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
