using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Agents.Definitions;
using SelfClaw.Desktop.Services.Agents.Models;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.ConversationInputs;

public sealed class ConversationInputLifecycleTests
{
    [Fact]
    public async Task Submission_awaits_the_shared_recovery_commit_before_acceptance_or_execution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new ConversationInputTestHarness(fixture: new(boundaryBeforeCommit: async (stage, token) => {
            if (stage == "recovery-before-commit") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        }));
        var id = await h.CreateConversationAsync(recover: false);
        var recovery = h.Dispatcher.RecoverOnceAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var submit = h.SubmitAsync(id, "A");
        try
        {
            submit.IsCompleted.Should().BeFalse();
            (await h.Fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(0L);
            h.Runtime.Prompts.Should().BeEmpty();
        }
        finally { release.TrySetResult(); }
        await recovery;
        (await submit).Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_with_a_failed_terminal_commit_keeps_the_persist_failure_reason()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        await h.SubmitAsync(id, "A");
        await h.Gated!.EnteredAsync("A").WaitAsync(TimeSpan.FromSeconds(10));
        await h.SubmitAsync(id, "B");
        var handle = h.Runs.GetActiveRun(id)!;
        h.Fixture.Turns.BeforeFinalizeCommit = (stage, _) => throw new System.IO.IOException(stage);
        await h.Service.StopAsync(id);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        (await h.Service.GetStateAsync(id)).PauseReason.Should().Be(ConversationInputReason.PersistFailed);
        h.Gated.Prompts.Should().Equal("A");
    }

    [Fact]
    public async Task Startup_holds_a_pending_steer_bound_to_an_old_running_turn()
    {
        using var fixture = new ConversationPersistenceFixture();
        var turn = await fixture.StartAsync();
        await fixture.SeedClaimAsync(turn.Turn, Guid.NewGuid(), 1, "old steer");
        await fixture.ExecuteAsync("UPDATE conversation_inputs SET status = 0;");
        await fixture.Inputs.RecoverStartupAsync();
        (await fixture.ScalarAsync("SELECT status FROM conversation_inputs;")).Should().Be(3L);
        (await fixture.ScalarAsync("SELECT status FROM conversation_turns;")).Should().Be(6L);
    }

    [Fact]
    public async Task Same_client_identity_survives_default_model_and_definition_changes_after_acceptance()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        var submission = h.Submission(id, "A") with { ModelProfileId = null };
        var first = await h.Service.SubmitAsync(submission);
        await h.Gated!.EnteredAsync("A").WaitAsync(TimeSpan.FromSeconds(10));
        h.Models.DefaultModel = Guid.NewGuid();
        var agent = h.Agents.ListAgents().Single(a => a.Id == "build");
        h.Agents.SaveAgent(new AgentEdit(agent.Id, agent.Name, agent.Description, agent.Mode, agent.Instructions + "\nchanged"));
        var duplicate = await h.Service.SubmitAsync(submission);
        duplicate.Accepted.Should().BeTrue();
        duplicate.InputId.Should().Be(first.InputId);
        var conflict = await h.Service.SubmitAsync(submission with { Prompt = "changed prompt" });
        conflict.Accepted.Should().BeFalse();
        conflict.Reason.Should().Be("request-conflict");
        (await h.Fixture.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 1;")).Should().Be(1L);
        h.Gated.Prompts.Should().Equal("A");
    }

    [Fact]
    public async Task Editing_a_pending_item_does_not_destroy_the_original_request_deduplication()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        await h.SubmitAsync(id, "A");
        await h.Gated!.EnteredAsync("A").WaitAsync(TimeSpan.FromSeconds(10));
        var request = h.Submission(id, "B");
        var accepted = await h.Service.SubmitAsync(request);
        await h.Service.EditAsync(accepted.InputId!.Value, accepted.Revision, "edited B");
        var duplicate = await h.Service.SubmitAsync(request);
        duplicate.Accepted.Should().BeTrue();
        duplicate.InputId.Should().Be(accepted.InputId);
        (await h.Fixture.Inputs.GetInputAsync(accepted.InputId.Value))!.Prompt.Should().Be("edited B");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_and_shutdown_cancel_and_drain_an_acceptance_before_it_can_commit(bool shutdown)
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Models.OnDefaultRead = async token => {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return h.Models.DefaultModel;
        };
        var submit = h.Service.SubmitAsync(h.Submission(id, "A") with { ModelProfileId = null });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (shutdown) await h.Runs.StopAsync(CancellationToken.None);
        else
        {
            var reservation = h.Runs.BeginDeletion(id);
            await h.Runs.DrainInputOperationsAsync(id, TimeSpan.FromSeconds(10));
            await h.Fixture.Conversations.DeleteConversationAsync(id);
            h.Runs.EndDeletion(reservation, true);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        (await h.Fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(0L);
        (await h.Service.SubmitAsync(h.Submission(id, "late"))).Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task Definition_fingerprint_survives_restart_and_detects_changed_file_even_when_revision_resets()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var conversation = (await h.Fixture.Conversations.GetConversationAsync(id))!;
        var snapshot = h.Snapshot();
        var restarted = new AgentSettingsService(new DesktopAgentDefinitionService(h.Fixture.Paths),
            new SubagentDefinitionCatalog(h.Fixture.Paths), new EmptyExtensionSettingsService(), new ExtensionStateChangeNotifier());
        var validator = new ConversationInputExecutionValidator(h.Roots, h.Models, restarted);
        (await validator.PrepareAsync(conversation, snapshot, CancellationToken.None)).Agent.Should().BeEquivalentTo(snapshot.AgentDefinition);
        var agent = h.Agents.ListAgents().Single(a => a.Id == "build");
        h.Agents.SaveAgent(new(agent.Id, agent.Name, agent.Description, agent.Mode, agent.Instructions + "\nnew"));
        restarted.Revision.Should().Be(snapshot.AgentRevision);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.PrepareAsync(conversation, snapshot, CancellationToken.None));
        error.Message.Should().Be(ConversationInputReason.AgentChanged);
    }

    [Fact]
    public async Task Retry_recaptures_a_real_changed_definition_and_resume_runs_the_new_frozen_definition()
    {
        using var h = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var id = await h.CreateConversationAsync();
        var input = await h.Fixture.AcceptFollowUpAsync(id, "A", snapshot: h.Snapshot());
        var agent = h.Agents.ListAgents().Single(a => a.Id == "build");
        h.Agents.SaveAgent(new(agent.Id, agent.Name, agent.Description, agent.Mode, agent.Instructions + "\nnew"));
        (await h.StartAsync(id)).Status.Should().Be(ConversationInputStartStatus.Failed);
        var held = (await h.Fixture.Inputs.GetInputAsync(input.Id))!;
        await h.Service.RetryAsync(input.Id, held.Revision);
        var state = await h.Service.GetStateAsync(id);
        state.Paused.Should().BeTrue();
        await h.Service.ResumeAsync(id, state.QueueRevision);
        await h.Gated!.EnteredAsync("A").WaitAsync(TimeSpan.FromSeconds(10));
        var consumed = (await h.Fixture.Inputs.GetInputAsync(input.Id))!;
        consumed.Status.Should().Be(ConversationInputStatus.Consumed);
        consumed.ExecutionSnapshot!.AgentDefinition!.Instructions.Should().EndWith("\nnew");
    }

    [Fact]
    public void The_actual_queue_envelope_never_exceeds_256KiB_and_items_never_include_full_body()
    {
        var items = Enumerable.Range(0, 500).Select(n => new ConversationInputQueueItem(Guid.NewGuid(), n,
            ConversationInputKind.FollowUp, ConversationInputStatus.Held, 1, new string('中', 65536),
            new string('中', 10000), new string('中', 10000))).ToArray();
        var state = new ConversationInputQueueState(Guid.NewGuid(), long.MaxValue, true, new string('中', 65536), false, items);
        var payload = ConversationQueueStateFormatter.Create(Guid.NewGuid(), state, new string('x', 128));
        WebViewHostChannel.SerializeToUtf8Bytes(payload).Length.Should().BeLessThanOrEqualTo(256 * 1024);
        payload.Truncated.Should().BeTrue();
        payload.Items.Should().OnlyContain(item => item.Preview.Length <= 200);
    }

    [Fact]
    public async Task Consumed_input_is_not_replayed_on_recovery_and_its_unfinished_turn_is_interrupted()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync(recover: false);
        var conversation = (await h.Fixture.Conversations.GetConversationAsync(id))!;
        var input = await h.Fixture.AcceptFollowUpAsync(id, "old", snapshot: h.Snapshot());
        var owner = Guid.NewGuid(); var claim = Guid.NewGuid();
        var claimed = await h.Fixture.ClaimNextAsync(id, owner, claim);
        var started = await h.Fixture.StartClaimedAsync(conversation, claimed, claim, owner);
        await h.Dispatcher.RecoverOnceAsync(CancellationToken.None);
        (await h.Fixture.Inputs.GetInputAsync(input.Id))!.Status.Should().Be(ConversationInputStatus.Consumed);
        (await h.Fixture.Turns.ListTurnsAsync(id)).Single(t => t.Id == started.Turn.Id).Status.Should().Be(ConversationTurnStatus.Interrupted);
        (await h.Service.GetStateAsync(id)).Items.Should().BeEmpty();
        h.Runtime.Prompts.Should().BeEmpty();
    }
}
