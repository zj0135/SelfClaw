using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

/// <summary>
/// P0 minimal verification for the ordered boundary event plus acknowledgement pattern that P2 will
/// build as <c>DirectTurnInputSession</c>. The harness is deliberately test-only: it proves the
/// producer/consumer/waiter contract (single waiter, ordered commit, bounded exit on failure or
/// cancellation) before the production shape is committed to.
/// </summary>
public sealed class InputBoundaryBarrierBaselineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ordered_reduction_and_commit_block_next_request_for_early_and_late_waiter(bool early)
    {
        var barrier = new BoundaryBarrier();
        var boundaryId = Guid.NewGuid();
        var observed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        Task<BoundaryCommitResult>? waiter = early ? barrier.AwaitBoundaryAsync(boundaryId, cancellation.Token) : null;
        var nextRequests = 0;
        var consumer = barrier.RunConsumerAsync(async (boundary, token) =>
        {
            barrier.ReducedText.Should().Be("ab");
            observed.Enqueue("commit-start");
            commitEntered.SetResult();
            await releaseCommit.Task.WaitAsync(token);
            observed.Enqueue("commit-end");
            return new BoundaryCommitResult(boundary.BoundaryId, "user-1");
        }, cancellation.Token);
        await barrier.WriteAsync(new TextDelta("a"));
        await barrier.WriteAsync(new TextDelta("b"));
        await barrier.WriteAsync(new BoundaryEvent(boundaryId, "claim-1"));
        await commitEntered.Task.WaitAsync(TestTimeout);
        waiter ??= barrier.AwaitBoundaryAsync(boundaryId, cancellation.Token);
        // Calling this directly runs as far as its incomplete await: no scheduling-based zero check.
        async Task ContinueAsync()
        {
            await waiter;
            observed.Enqueue("next-request");
            Interlocked.Increment(ref nextRequests);
            barrier.CompleteProducer();
        }
        var continuation = ContinueAsync();
        continuation.IsCompleted.Should().BeFalse();
        barrier.PendingWaiterCount.Should().Be(1);
        nextRequests.Should().Be(0);
        releaseCommit.SetResult();
        await Task.WhenAll(consumer, continuation).WaitAsync(TestTimeout);
        nextRequests.Should().Be(1);
        observed.Should().Equal("commit-start", "commit-end", "next-request");
        barrier.PendingWaiterCount.Should().Be(0);
    }

    [Theory]
    [InlineData("failure", true)]
    [InlineData("failure", false)]
    [InlineData("cancel", true)]
    [InlineData("cancel", false)]
    [InlineData("close", true)]
    [InlineData("close", false)]
    public async Task Consumer_terminal_state_releases_early_and_late_waiters(string state, bool early)
    {
        var barrier = new BoundaryBarrier();
        var boundaryId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var waiter = early ? barrier.AwaitBoundaryAsync(boundaryId, CancellationToken.None) : null;
        if (early) barrier.PendingWaiterCount.Should().Be(1);
        var consumer = barrier.RunConsumerAsync(
            (_, _) => throw new InvalidOperationException("commit failed"), cancellation.Token);
        if (state == "failure")
            await barrier.WriteAsync(new BoundaryEvent(boundaryId, "claim-1"));
        else if (state == "cancel")
            cancellation.Cancel();
        else
            barrier.CompleteProducer();
        var consumerError = await Record.ExceptionAsync(() => consumer.WaitAsync(TestTimeout));
        if (state == "failure") consumerError.Should().BeOfType<InvalidOperationException>();
        if (state == "cancel") consumerError.Should().BeAssignableTo<OperationCanceledException>();
        if (state == "close") consumerError.Should().BeNull();
        // Awaiting consumer completion forces its terminal-state write before late registration.
        waiter ??= barrier.AwaitBoundaryAsync(boundaryId, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => waiter.WaitAsync(TestTimeout));
        if (state == "cancel") failure.Should().BeAssignableTo<OperationCanceledException>();
        else failure.Should().BeOfType<InvalidOperationException>();
        failure.Should().NotBeOfType<TimeoutException>();
        barrier.PendingWaiterCount.Should().Be(0);
    }

    [Fact]
    public async Task A_second_distinct_boundary_waiter_is_rejected_and_cancellation_releases_the_first()
    {
        var barrier = new BoundaryBarrier();
        using var cancellation = new CancellationTokenSource();
        var waiter = barrier.AwaitBoundaryAsync(Guid.NewGuid(), cancellation.Token);
        barrier.PendingWaiterCount.Should().Be(1);
        await FluentActions.Awaiting(() => barrier.AwaitBoundaryAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*one*waiter*");
        cancellation.Cancel();
        await FluentActions.Awaiting(() => waiter).Should().ThrowAsync<OperationCanceledException>();
        barrier.PendingWaiterCount.Should().Be(0);
    }

    [Fact]
    public async Task Reconfirming_the_same_boundary_id_reuses_the_committed_result()
    {
        var barrier = new BoundaryBarrier();
        var boundaryId = Guid.NewGuid();
        var commits = 0;
        using var cancellation = new CancellationTokenSource(TestTimeout);

        var consumer = barrier.RunConsumerAsync(
            (boundary, _) =>
            {
                Interlocked.Increment(ref commits);
                return Task.FromResult(new BoundaryCommitResult(boundary.BoundaryId, "user-1"));
            },
            cancellation.Token);

        await barrier.WriteAsync(new BoundaryEvent(boundaryId, "claim-1"));
        await barrier.CommitPublished.Task.WaitAsync(TestTimeout);
        barrier.PendingWaiterCount.Should().Be(0);
        var first = await barrier.AwaitBoundaryAsync(boundaryId, cancellation.Token);
        var second = await barrier.AwaitBoundaryAsync(boundaryId, cancellation.Token);
        barrier.CompleteProducer();
        await consumer.WaitAsync(TestTimeout);

        first.Should().Be(second);
        commits.Should().Be(1);
    }

    private sealed record TextDelta(string Text);

    private sealed record BoundaryEvent(Guid BoundaryId, string ClaimId);

    private sealed record BoundaryCommitResult(Guid BoundaryId, string UserMessageId);

    /// <summary>
    /// Single-writer, single-reader, single-waiter prototype of the ordered boundary barrier. The
    /// boundary payload is pure data; the waiter lives here, not in the event.
    /// </summary>
    private sealed class BoundaryBarrier
    {
        private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        private readonly object _gate = new();
        private readonly Dictionary<Guid, TaskCompletionSource<BoundaryCommitResult>> _waiters = [];
        private readonly Dictionary<Guid, BoundaryCommitResult> _committed = [];
        private Exception? _failure;
        internal string ReducedText { get; private set; } = string.Empty;
        internal TaskCompletionSource CommitPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int PendingWaiterCount
        {
            get
            {
                lock (_gate)
                {
                    return _waiters.Count;
                }
            }
        }

        internal ValueTask WriteAsync(object streamEvent) => _events.Writer.WriteAsync(streamEvent);

        internal void CompleteProducer() => _events.Writer.TryComplete();

        internal async Task<BoundaryCommitResult> AwaitBoundaryAsync(Guid boundaryId, CancellationToken cancellationToken)
        {
            TaskCompletionSource<BoundaryCommitResult> waiter;
            Exception? failure = null;
            lock (_gate)
            {
                if (_committed.TryGetValue(boundaryId, out var committed))
                {
                    return committed;
                }

                failure = _failure;
                if (failure is null)
                {
                    if (_waiters.Count != 0)
                    {
                        throw new InvalidOperationException("Only one pending waiter per turn is allowed.");
                    }

                    waiter = new TaskCompletionSource<BoundaryCommitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[boundaryId] = waiter;
                }
                else
                {
                    waiter = null!;
                }
            }

            if (failure is not null)
            {
                // A late waiter must observe a barrier that already failed rather than wait forever.
                return await Task.FromException<BoundaryCommitResult>(failure).ConfigureAwait(false);
            }

            try
            {
                using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
                return await waiter.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _waiters.Remove(boundaryId);
                }
            }
        }

        internal async Task RunConsumerAsync(
            Func<BoundaryEvent, CancellationToken, Task<BoundaryCommitResult>> commit,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var streamEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (streamEvent is TextDelta text)
                        ReducedText += text.Text;
                    if (streamEvent is not BoundaryEvent boundary)
                    {
                        continue;
                    }

                    var result = await commit(boundary, cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _committed[boundary.BoundaryId] = result;
                        if (_waiters.Remove(boundary.BoundaryId, out var waiter))
                        {
                            waiter.TrySetResult(result);
                        }
                    }
                    CommitPublished.TrySetResult();
                }

                Fail(new InvalidOperationException("The producer ended without committing the boundary."));
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        }

        private void Fail(Exception exception)
        {
            lock (_gate)
            {
                _failure ??= exception;
                foreach (var pair in _waiters)
                {
                    pair.Value.TrySetException(_failure);
                }

                _waiters.Clear();
            }
        }
    }
}