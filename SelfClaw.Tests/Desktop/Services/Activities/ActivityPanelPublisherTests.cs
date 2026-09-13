using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityPanelPublisherTests
{
    [Fact]
    public Task Correlated_snapshot_and_live_push_are_independent_of_parent_transcript() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var activity = new SubagentActivityTestContext();
        var task = await activity.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = activity.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task;
        using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId);
        var subscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "initial", CancellationToken.None);
        panel.LatestState.GetProperty("requestId").GetString().Should().Be("initial");
        panel.LatestState.GetProperty("sections")[0].GetProperty("counts").GetProperty("running").GetInt32().Should().Be(1);
        panel.AcknowledgeLatest();
        await panel.Publisher.SelectDetailAsync(subscription, Guid.NewGuid(), task.Id, null, null, "detail", CancellationToken.None);
        panel.AcknowledgeLatest();
        await panel.Publisher.GetStateAsync(subscription, null, "intermediate", CancellationToken.None);
        await runtime.EmitAsync(new AssistantThinkingDeltaEvent("thinking", "visible before terminal"));
        await UntilAsync(panel, () => panel.LatestState.ToString().Contains("visible before terminal", StringComparison.Ordinal));
        panel.LatestState.GetProperty("sections")[0].GetProperty("detail").GetProperty("contentOrigin").GetString().Should().Be("live");
        execution.IsCompleted.Should().BeFalse();
        panel.Channel.PublishTranscript(new TranscriptRenderState([], false, [], task.ParentConversationId.ToString("D"), false));
        var transcript = panel.Messages.Last(message => message.GetProperty("type").GetString() == "replaceState");
        panel.Channel.AcknowledgeTranscript(transcript.GetProperty("revision").GetInt64()).Should().BeTrue();
        panel.Channel.PublishTranscript(new TranscriptRenderState([], false, [], task.ParentConversationId.ToString("D"), false, ActivityText: "changed"));
        panel.Messages.Last().GetProperty("type").GetString().Should().Be("patchState");
        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
        await execution;
        panel.AcknowledgeLatest();
        await UntilAsync(panel, () => panel.LatestState.ToString().Contains("persisted", StringComparison.Ordinal));
        panel.LatestState.GetProperty("sections")[0].GetProperty("detail").GetProperty("task").GetProperty("status").GetString().Should().Be("succeeded");
    });

    [Fact]
    public Task Switching_detail_during_read_drops_old_selection_before_assigning_revision() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var activity = new SubagentActivityTestContext();
        var task = await activity.CreateTaskAsync(claim: false);
        using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId);
        var subscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "initial", CancellationToken.None);
        panel.AcknowledgeLatest();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.Reader.AfterDetailReadAsync = async () => { entered.TrySetResult(); await release.Task; };
        var old = panel.Publisher.SelectDetailAsync(subscription, Guid.NewGuid(), task.Id, null, null, "old", CancellationToken.None);
        await entered.Task;
        var currentSelection = Guid.NewGuid();
        var current = panel.Publisher.SelectDetailAsync(subscription, currentSelection, null, null, null, "new", CancellationToken.None);
        release.TrySetResult();
        await Task.WhenAll(old, current);
        panel.Messages.Any(message => message.TryGetProperty("requestId", out var id) && id.GetString() == "old" && message.TryGetProperty("error", out _)).Should().BeTrue();
        panel.AcknowledgeLatest();
        panel.LatestState.GetProperty("sections")[0].GetProperty("detailSelectionId").GetGuid().Should().Be(currentSelection);
        panel.LatestState.GetProperty("sections")[0].TryGetProperty("detail", out _).Should().BeFalse();
    });

    [Fact]
    public Task Navigation_reset_and_A_B_A_require_new_subscriptions_and_empty_scope_is_legal() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var activity = new SubagentActivityTestContext();
        var first = await activity.CreateTaskAsync(claim: false);
        var second = await activity.CreateTaskAsync(claim: false);
        using var panel = new ActivityPanelTestContext(activity, first.ParentConversationId);
        var oldSubscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(oldSubscription, first.ParentConversationId, "first", CancellationToken.None);
        var oldRevision = panel.LatestState.GetProperty("revision").GetInt64();
        panel.Source.Select(second.ParentConversationId);
        panel.Source.Select(first.ParentConversationId);
        panel.Publisher.Acknowledge(oldSubscription, oldRevision).Should().BeFalse();
        panel.Channel.MarkNotReady();
        panel.Channel.MarkReady();
        var replacement = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(replacement, first.ParentConversationId, "reload", CancellationToken.None);
        panel.LatestState.GetProperty("subscriptionId").GetGuid().Should().Be(replacement);
        panel.Source.Select(null);
        await panel.Publisher.SubscribeAsync(Guid.NewGuid(), null, "empty", CancellationToken.None);
        panel.LatestState.GetProperty("sections").GetArrayLength().Should().Be(0);
        panel.LatestState.TryGetProperty("parentConversationId", out _).Should().BeFalse();
    });

    [Fact]
    public Task Ack_timeout_retransmits_three_times_then_retains_only_latest_until_resumed() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var activity = new SubagentActivityTestContext();
        var task = await activity.CreateTaskAsync(claim: false);
        using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId);
        var subscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "initial", CancellationToken.None);
        var firstRevision = panel.LatestState.GetProperty("revision").GetInt64();
        for (var index = 0; index < 10; index++)
            await panel.Publisher.GetStateAsync(subscription, null, $"refresh-{index}", CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(8.5));
        var snapshots = panel.Messages.Where(item => item.GetProperty("type").GetString() == "activity-panel/state").ToArray();
        snapshots.Should().HaveCount(4);
        snapshots.Should().OnlyContain(item => item.GetProperty("revision").GetInt64() == firstRevision);
        panel.Publisher.Acknowledge(subscription, firstRevision).Should().BeTrue();
        panel.LatestState.GetProperty("revision").GetInt64().Should().BeGreaterThan(firstRevision);
        panel.Publisher.Acknowledge(subscription, firstRevision).Should().BeFalse();
    });

    private static async Task UntilAsync(ActivityPanelTestContext panel, Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            // Like the WebView reader, acknowledge intermediate states so the pending snapshot can advance.
            panel.AcknowledgeLatest();
            await Task.Delay(10, timeout.Token);
        }
    }
}
