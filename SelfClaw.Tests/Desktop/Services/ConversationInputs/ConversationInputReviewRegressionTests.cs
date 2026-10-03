using System.Reflection;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.ConversationInputs;

public sealed class ConversationInputReviewRegressionTests
{
    [Fact]
    public async Task Running_A_excludes_B_C_and_each_followup_has_its_own_turn()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        await h.SubmitAsync(id, "A");
        await h.Gated!.EnteredAsync("A").WaitAsync(TimeSpan.FromSeconds(10));
        await h.SubmitAsync(id, "B"); await h.SubmitAsync(id, "C");
        (await h.StartAsync(id)).Status.Should().Be(ConversationInputStartStatus.Idle);
        h.Gated.Prompts.Should().Equal("A");
        (await h.Fixture.Conversations.ListMessagesAsync(id)).Where(m => m.Role == MessageRole.User)
            .Select(m => m.MarkdownContent).Should().Equal("A");
        var a = h.Runs.GetActiveRun(id)!;
        h.Gated.Release("A");
        await h.Gated.EnteredAsync("B").WaitAsync(TimeSpan.FromSeconds(10));
        await a.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var b = h.Runs.GetActiveRun(id)!;
        h.Gated.Release("B");
        await h.Gated.EnteredAsync("C").WaitAsync(TimeSpan.FromSeconds(10));
        await b.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var c = h.Runs.GetActiveRun(id)!;
        h.Gated.Release("C"); await c.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var users = (await h.Fixture.Conversations.ListMessagesAsync(id)).Where(m => m.Role == MessageRole.User).ToArray();
        users.Select(m => m.MarkdownContent).Should().Equal("A", "B", "C");
        users.Select(m => m.TurnId).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task Idle_new_D_starts_existing_B_and_preserves_C_D_order()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        await h.Fixture.AcceptFollowUpAsync(id, "B", snapshot: Snapshot(h));
        await h.Fixture.AcceptFollowUpAsync(id, "C", snapshot: Snapshot(h));
        await h.SubmitAsync(id, "D");
        await h.Gated!.EnteredAsync("B").WaitAsync(TimeSpan.FromSeconds(10));
        h.Gated.Prompts.Should().Equal("B");
        (await h.Service.GetStateAsync(id)).Items.Select(i => i.Preview).Should().Equal("C", "D");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Explicit_edit_cancel_claim_order(bool claimFirst, bool cancel)
    {
        using var f = new ConversationPersistenceFixture();
        var c = await f.CreateConversationAsync();
        var input = await f.AcceptFollowUpAsync(c.Id, "old");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => {
            if (claimFirst) await f.ClaimNextAsync(c.Id, Guid.NewGuid(), Guid.NewGuid());
            else if (cancel) await f.Inputs.CancelAsync(new(input.Id, input.Revision));
            else await f.Inputs.EditAsync(new(input.Id, input.Revision, "new"));
            barrier.SetResult();
        });
        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (claimFirst)
        {
            var current = (await f.Inputs.GetInputAsync(input.Id))!;
            var result = cancel ? await f.Inputs.CancelAsync(new(input.Id, current.Revision))
                : await f.Inputs.EditAsync(new(input.Id, current.Revision, "new"));
            result.Status.Should().Be(ConversationInputUpdateStatus.Conflict);
        }
        else
        {
            var claim = await f.Inputs.TryClaimNextFollowUpAsync(c.Id, Guid.NewGuid(), Guid.NewGuid());
            if (cancel) claim.Should().BeNull(); else claim!.Prompt.Should().Be("new");
        }
        await first;
    }

    [Fact]
    public async Task Paused_Held_parent_blocks_only_its_own_continuation()
    {
        using var h = new ConversationInputTestHarness();
        var c = await h.Fixture.CreateConversationAsync(); var other = await h.Fixture.CreateConversationAsync();
        var input = await h.Fixture.AcceptFollowUpAsync(c.Id, "A");
        await h.Fixture.Inputs.HoldAsync(input.Id, input.Revision, "held", null);
        var s = await h.Service.GetStateAsync(c.Id); await h.Service.PauseAsync(c.Id, s.QueueRevision, "paused");
        (await h.Runs.TryReserveContinuationAsync(c)).Should().BeNull();
        var handle = await h.Runs.TryReserveContinuationAsync(other);
        handle.Should().NotBeNull(); h.Runs.Complete(handle!, false);
    }

    [Fact]
    public async Task Concurrent_acceptance_enforces_20_and_utf8_64KiB()
    {
        using var f = new ConversationPersistenceFixture();
        var c = await f.CreateConversationAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = Enumerable.Range(0, 22).Select(n => Task.Run(async () => {
            await gate.Task;
            return await f.Inputs.AcceptAsync(new(c.Id, n.ToString(), ConversationInputKind.FollowUp, null, "A", f.Snapshot(), null, true));
        })).ToArray();
        gate.SetResult(); var results = await Task.WhenAll(jobs);
        results.Count(r => r.Status == ConversationInputAcceptStatus.Accepted).Should().Be(20);
        results.Count(r => r.Status == ConversationInputAcceptStatus.Full).Should().Be(2);
        var other = await f.CreateConversationAsync();
        await f.AcceptFollowUpAsync(other.Id, new string('中', 21845) + "x");
        await Assert.ThrowsAsync<ArgumentException>(() => f.Inputs.AcceptAsync(new(other.Id, "too-big", ConversationInputKind.FollowUp, null,
            new string('中', 21845) + "xx", f.Snapshot(), null, true)));
    }

    [Fact]
    public Task Vue_subscription_identity_must_be_accepted_by_real_bridge()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var h = new ConversationInputTestHarness();
            var id = await h.CreateConversationAsync();
            var channel = new SelfClaw.Desktop.Services.WebView.WebViewHostChannel();
            var posted = new List<string>();
            channel.Attach(posted.Add);
            using var publisher = new ConversationInputPublisher(h.Fixture.Inputs, h.Changes, channel,
                System.Windows.Threading.Dispatcher.CurrentDispatcher,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationInputPublisher>.Instance);
            var bridge = new ConversationInputBridge(h.Service, publisher, channel, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            using var payload = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new {
                requestId = "review", subscriptionId = Guid.NewGuid().ToString("D"), conversationId = id
            }));
            await bridge.HandleAsync("conversation-input/subscribe", payload.RootElement, CancellationToken.None);
            posted.Last().Should().Contain("conversation-input/state", "the exact Vue subscription shape must work against the host");
        });

    [Fact]
    public void Published_item_status_must_match_Vue_string_contract()
    {
        var item = new ConversationInputQueueItem(Guid.NewGuid(), 1, ConversationInputKind.FollowUp,
            ConversationInputStatus.Held, 1, "A", null, null);
        var state = new ConversationInputQueueState(Guid.NewGuid(), 1, false, null, false, [item]);
        var payload = ConversationQueueStateFormatter.Create(Guid.NewGuid(), state, null);
        var bytes = SelfClaw.Desktop.Services.WebView.WebViewHostChannel.SerializeToUtf8Bytes(payload);
        System.Text.Encoding.UTF8.GetString(bytes).Should().Contain("\"status\":\"held\"");
    }

    [Fact]
    public async Task Deleted_conversation_must_not_be_recreated_by_late_acceptance()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var conversation = await h.Fixture.Conversations.GetConversationAsync(id);
        var deletion = h.Runs.BeginDeletion(id);
        await h.Fixture.Conversations.DeleteConversationAsync(id);
        h.Runs.EndDeletion(deletion, true);
        var result = await h.Service.SubmitAsync(h.Submission(id, "late") with { Conversation = conversation });
        result.Accepted.Should().BeFalse("late UI preparation must not resurrect a tombstoned conversation");
    }

    [Fact]
    public async Task Missing_physical_root_must_hold_before_runtime()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        var now = DateTimeOffset.UtcNow;
        var root = new WorkspaceRoot(Guid.NewGuid(), "missing", System.IO.Path.Combine(h.Fixture.RootPath, "never-created"), now, now);
        await h.Roots.UpsertWorkspaceRootAsync(root);
        var conversation = (await h.Fixture.Conversations.GetConversationAsync(id))!;
        await h.Fixture.Conversations.UpsertConversationAsync(conversation with { WorkspaceRootId = root.Id });
        await h.Fixture.AcceptFollowUpAsync(id, "A", snapshot: Snapshot(h) with { WorkspaceRootId = root.Id, WorkspaceRootPath = root.RootPath });
        var attempt = await h.StartAsync(id);
        attempt.Status.Should().Be(ConversationInputStartStatus.Failed, "a database row does not prove the directory exists");
    }

    [Theory]
    [InlineData("conversation_turns", "INSERT")]
    [InlineData("messages", "INSERT")]
    [InlineData("conversation_inputs", "UPDATE")]
    [InlineData("conversation_input_state", "UPDATE")]
    public async Task Consumption_write_fault_rolls_back_at_each_table(string table, string operation)
    {
        using var f = new ConversationPersistenceFixture();
        var conversation = await f.CreateConversationAsync();
        await f.AcceptFollowUpAsync(conversation.Id, "A");
        var owner = Guid.NewGuid(); var claimId = Guid.NewGuid();
        var input = await f.ClaimNextAsync(conversation.Id, owner, claimId);
        await f.ExecuteAsync($"CREATE TRIGGER review_fault BEFORE {operation} ON {table} BEGIN SELECT RAISE(ABORT, 'review-fault'); END;");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => f.StartClaimedAsync(conversation, input, claimId, owner));
        (await f.ScalarAsync("SELECT COUNT(*) FROM conversation_turns;")).Should().Be(0L);
        (await f.ScalarAsync("SELECT COUNT(*) FROM messages;")).Should().Be(0L);
        (await f.Inputs.GetInputAsync(input.Id))!.Status.Should().Be(ConversationInputStatus.Claimed);
    }

    private static ConversationInputExecutionSnapshot Snapshot(ConversationInputTestHarness h)
        => h.Snapshot();

    [Fact]
    public async Task Held_head_must_block_following_pending_after_resume()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var a = await h.Fixture.AcceptFollowUpAsync(id, "A", snapshot: Snapshot(h));
        await h.Fixture.Inputs.HoldAsync(a.Id, a.Revision, "held", null);
        await h.Fixture.AcceptFollowUpAsync(id, "B", snapshot: Snapshot(h));
        var claimed = await h.Fixture.Inputs.TryClaimNextFollowUpAsync(id, Guid.NewGuid(), Guid.NewGuid());
        claimed.Should().BeNull("Held A must block B even when the queue is unpaused");
    }

    [Fact]
    public async Task Disabled_feature_must_not_resume_persisted_backlog()
    {
        using var h = new ConversationInputTestHarness(queueEnabled: false, runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        await h.Fixture.AcceptFollowUpAsync(id, "A", snapshot: Snapshot(h));
        await h.Dispatcher.RecoverOnceAsync(CancellationToken.None);
        var state = await h.Service.GetStateAsync(id);
        await h.Service.ResumeAsync(id, state.QueueRevision);
        var record = await h.Fixture.Inputs.GetQueueStateAsync(id);
        h.Runs.IsRunning(id).Should().BeFalse("a disabled Queue must not dispatch a persisted backlog through resume");
    }

    [Fact]
    public async Task Scan_must_reach_candidate_after_32_paused_parents()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        for (var n = 0; n < 33; n++)
        {
            var id = await h.CreateConversationAsync();
            await h.Fixture.AcceptFollowUpAsync(id, "A", snapshot: Snapshot(h));
        }
        var ids = await h.Fixture.Inputs.ListDispatchableConversationIdsAsync();
        foreach (var id in ids.Take(32))
        {
            var s = await h.Service.GetStateAsync(id);
            await h.Service.PauseAsync(id, s.QueueRevision, "paused");
        }
        var scan = typeof(ConversationInputDispatcher).GetMethod("ScanOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (var n = 0; n < 3; n++)
            await (Task<bool>)scan.Invoke(h.Dispatcher, [CancellationToken.None])!;
        h.Runs.IsRunning(ids[32]).Should().BeTrue("completed scans must not starve the 33rd parent behind paused parents");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_terminal_must_pause_before_releasing_admission(bool persistFailure)
    {
        using var h = new ConversationInputTestHarness();
        if (persistFailure) h.Fixture.Turns.BeforeFinalizeCommit = (stage, _) => throw new System.IO.IOException(stage);
        else h.Runtime.FailPrompts.Add("A");
        var id = await h.CreateConversationAsync();
        await h.Fixture.AcceptFollowUpAsync(id, "A", snapshot: Snapshot(h));
        await h.Fixture.AcceptFollowUpAsync(id, "B", snapshot: Snapshot(h));
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        h.Runs.Changed += change =>
        {
            if (change.IsCompleted && Interlocked.Increment(ref first) == 1)
            {
                released.TrySetResult();
                allowReturn.Task.GetAwaiter().GetResult();
            }
        };
        try
        {
            await h.StartAsync(id);
            await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var attempt = await h.StartAsync(id);
            attempt.Status.Should().NotBe(ConversationInputStartStatus.Started,
                "a scan in the completion window must not admit B after A failed");
        }
        finally { allowReturn.TrySetResult(); }
    }

    [Fact]
    public async Task Retry_must_recapture_changed_agent_snapshot()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var a = await h.Fixture.AcceptFollowUpAsync(id, "A", snapshot: Snapshot(h) with { AgentRevision = h.Agents.Revision + 1, DefinitionHash = "outdated" });
        await h.StartAsync(id);
        var held = (await h.Fixture.Inputs.GetInputAsync(a.Id))!;
        held.Status.Should().Be(ConversationInputStatus.Held);
        await h.Service.RetryAsync(a.Id, held.Revision);
        var retried = (await h.Fixture.Inputs.GetInputAsync(a.Id))!;
        retried.ExecutionSnapshot!.AgentRevision.Should().Be(h.Agents.Revision,
            "retry explicitly recaptures the changed definition instead of repeatedly holding the same snapshot");
    }

    [Fact]
    public async Task Global_limit_must_reject_501st_in_a_new_conversation()
    {
        using var f = new ConversationPersistenceFixture();
        for (var n = 0; n < 25; n++)
        {
            var c = await f.CreateConversationAsync();
            for (var j = 0; j < 20; j++) await f.AcceptFollowUpAsync(c.Id, "A");
        }
        var extra = await f.CreateConversationAsync();
        var result = await f.Inputs.AcceptAsync(new(extra.Id, "extra", ConversationInputKind.FollowUp, null, "A", f.Snapshot(), null, true));
        result.Status.Should().Be(ConversationInputAcceptStatus.Full);
    }

    [Fact]
    public async Task Pause_revision_conflict_must_not_be_treated_as_paused()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        await h.Fixture.AcceptFollowUpAsync(id, "A", snapshot: Snapshot(h));
        var state = await h.Service.GetStateAsync(id);
        await h.Fixture.AcceptFollowUpAsync(id, "B", snapshot: Snapshot(h));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.PauseAsync(id, state.QueueRevision, "stop"));
        await h.Service.StopAsync(id);
        (await h.Service.GetStateAsync(id)).Paused.Should().BeTrue("system stop uses an unconditional durable pause");
    }
}
