using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.ConversationInputs;

/// <summary>
/// Esc must reach the turn's cancellation token promptly - that token is what kills the running tool
/// process, and the call happens on the UI thread - even when the database cannot take the two settle
/// writes. The settle sequence (close steer intake, drop the backlog, then cancel) keeps its order; what
/// these tests pin is that a busy database cannot turn it into a minute-long stop.
/// </summary>
public sealed class ConversationInputStopLatencyTests
{
    [Fact]
    public async Task A_busy_write_lock_does_not_delay_the_cancel()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        await h.SubmitAsync(id, "A");
        await h.Gated!.EnteredAsync("A").WaitAsync(TimeSpan.FromSeconds(10));
        await h.SubmitAsync(id, "B");
        var handle = h.Runs.GetActiveRun(id)!;

        await using var blocker = await h.Fixture.Database.OpenConnectionAsync();
        await using var transaction = blocker.BeginTransaction(deferred: false);
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE conversation_input_state SET queue_revision = queue_revision WHERE 1 = 0;";
            await command.ExecuteNonQueryAsync();
        }

        // Before the settle was bounded and moved off the calling thread, the synchronous BEGIN IMMEDIATE
        // inside it held the caller (the UI thread) for the whole busy timeout and kept the turn - and the
        // tool process it was running - alive for that long.
        var timer = Stopwatch.StartNew();
        var stop = h.Service.StopAsync(id);
        var cancelledAfter = await CancelledWithinAsync(handle, TimeSpan.FromSeconds(20), timer);

        cancelledAfter.Should().BeLessThan(TimeSpan.FromSeconds(10).TotalMilliseconds,
            "the cancel is what kills the running tool process, so it cannot wait for the settle to commit");

        // Releasing the lock lets the settle finish, and a settle that did commit is honoured.
        await transaction.RollbackAsync();
        await stop.WaitAsync(TimeSpan.FromSeconds(60));
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        handle.UserInitiatedStop.Should().BeTrue();
        var state = await h.Service.GetStateAsync(id);
        state.Paused.Should().BeFalse();
        state.Items.Should().BeEmpty("a user stop drops the backlog instead of pausing behind it");
    }

    [Fact]
    public async Task A_healthy_stop_still_drops_the_backlog_and_leaves_the_queue_running()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        await h.SubmitAsync(id, "A");
        await h.Gated!.EnteredAsync("A").WaitAsync(TimeSpan.FromSeconds(10));
        await h.SubmitAsync(id, "B");
        var handle = h.Runs.GetActiveRun(id)!;

        await h.Service.StopAsync(id);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        handle.UserInitiatedStop.Should().BeTrue();
        var state = await h.Service.GetStateAsync(id);
        state.Paused.Should().BeFalse();
        state.Items.Should().BeEmpty("a user stop drops the backlog instead of pausing behind it");
        (await h.Service.SubmitAsync(h.Submission(id, "C"))).Accepted.Should().BeTrue();
    }

    private static async Task<double> CancelledWithinAsync(ConversationRunHandle handle, TimeSpan budget,
        Stopwatch sinceStopRequested)
    {
        var cancelled = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = handle.CancellationToken.Register(
            () => cancelled.TrySetResult(sinceStopRequested.ElapsedMilliseconds));
        return await Task.WhenAny(cancelled.Task, Task.Delay(budget)) == cancelled.Task
            ? await cancelled.Task
            : double.MaxValue;
    }
}
