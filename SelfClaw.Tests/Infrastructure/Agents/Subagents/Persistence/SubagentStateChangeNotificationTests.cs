using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Subagents.Persistence;

public sealed class SubagentStateChangeNotificationTests
{
    [Fact]
    public async Task Task_notifications_cover_queued_claim_cancel_terminal_and_initial_failure_but_not_rollback()
    {
        using var context = new SubagentActivityTestContext();
        var notifications = new ConcurrentQueue<SubagentStateChange>();
        context.Changes.Changed += notifications.Enqueue;
        context.Changes.Changed += _ => throw new InvalidOperationException("Observer failed.");
        var queued = await context.CreateTaskAsync(claim: false);
        context.Changes.PersistenceRevision.Should().Be(1);
        var claimed = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("Missing claim.");
        context.Changes.PersistenceRevision.Should().Be(2);
        await context.Tasks.RequestCancellationAsync(queued.ParentConversationId, queued.Id, DateTimeOffset.UtcNow);
        context.Changes.PersistenceRevision.Should().Be(3);
        var completion = Completion(claimed, SubagentTaskStatus.Cancelled);
        (await context.Tasks.TryCompleteAsync(claimed.Id, SubagentTaskStatus.Queued, completion)).Should().BeNull();
        context.Changes.PersistenceRevision.Should().Be(3);
        await context.Tasks.TryCompleteAsync(claimed.Id, SubagentTaskStatus.Running, completion);
        context.Changes.PersistenceRevision.Should().Be(4);

        var creation = CloneCreation(queued);
        await context.Tasks.CreateAsync(creation with { InitialCompletion = Completion(creation.Task, SubagentTaskStatus.Failed) });
        context.Changes.PersistenceRevision.Should().Be(5);
        var invalid = CloneCreation(queued);
        var rollback = () => context.Tasks.CreateAsync(invalid with { Task = invalid.Task with { MaxRunSeconds = 1 } });
        await rollback.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
        context.Changes.PersistenceRevision.Should().Be(5);
        (await context.Conversations.GetConversationAsync(invalid.ChildConversation.Id)).Should().BeNull();
        await WaitForCountAsync(notifications, 5);
        notifications.Should().HaveCount(5).And.OnlyContain(change => change.Kind == SubagentStateChangeKind.Task);
        var list = await context.Service.ListAsync(new SubagentActivityQuery(queued.ParentConversationId));
        list.Page.Counts.Cancelled.Should().Be(1);
        list.Page.Counts.Failed.Should().Be(1);
        (await context.Tasks.GetDeliveryAsync(queued.ParentConversationId, creation.Task.Id)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivery_notifications_cover_lease_renew_resolve_and_expired_recovery(bool succeed)
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync(claim: false);
        await context.Tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Queued, Completion(task, SubagentTaskStatus.Cancelled));
        var deliveries = new SqliteSubagentDeliveryRepository(context.Database,
            NullLogger<SqliteSubagentDeliveryRepository>.Instance, context.Changes);
        var now = DateTimeOffset.UtcNow.AddSeconds(3);
        var mailbox = await deliveries.PeekReadyMailboxAsync(now, now) ?? throw new InvalidOperationException("Missing mailbox.");
        var lease = await deliveries.TryLeaseBatchAsync(mailbox, Guid.NewGuid(), Guid.NewGuid(), now, now.AddSeconds(45), 64 * 1024)
            ?? throw new InvalidOperationException("Missing lease.");
        context.Changes.PersistenceRevision.Should().Be(3);
        await deliveries.TryRenewLeaseAsync(lease, now.AddSeconds(5), now.AddSeconds(50));
        context.Changes.PersistenceRevision.Should().Be(4);
        if (succeed)
        {
            var message = new MessageRecord(lease.ContinuationTurnId, task.ParentConversationId, MessageRole.Assistant,
                "continued", MessageStatus.Completed, now, now);
            await deliveries.TryResolveAsync(lease,
                new SubagentDeliveryResolution(SubagentDeliveryResolutionKind.Succeeded, new TurnFinalization(message, []), null, now));
            context.Changes.PersistenceRevision.Should().Be(5);
            (await context.Service.GetDetailAsync(task.ParentConversationId, task.Id))?.Detail.Task.DeliveryStatus.Should().Be(SubagentDeliveryStatus.Delivered);
        }
        else
        {
            await deliveries.RecoverExpiredLeasesAsync(now.AddMinutes(1));
            context.Changes.PersistenceRevision.Should().Be(5);
            var ready = now.AddMinutes(2);
            mailbox = await deliveries.PeekReadyMailboxAsync(ready, ready) ?? throw new InvalidOperationException("Missing recovered mailbox.");
            lease = await deliveries.TryLeaseBatchAsync(mailbox, Guid.NewGuid(), Guid.NewGuid(), ready, ready.AddSeconds(45), 64 * 1024)
                ?? throw new InvalidOperationException("Missing recovered lease.");
            await deliveries.TryResolveAsync(lease,
                new SubagentDeliveryResolution(SubagentDeliveryResolutionKind.DeadLetter, null, "Cannot continue.", ready));
            context.Changes.PersistenceRevision.Should().Be(7);
            (await context.Service.GetDetailAsync(task.ParentConversationId, task.Id))?.Detail.Task.DeliveryStatus.Should().Be(SubagentDeliveryStatus.DeadLetter);
        }

        var revision = context.Changes.PersistenceRevision;
        (await deliveries.TryRenewLeaseAsync(lease, now, now.AddHours(1))).Should().BeFalse();
        await deliveries.RecoverExpiredLeasesAsync(now.AddHours(2));
        context.Changes.PersistenceRevision.Should().Be(revision);
    }

    private static SubagentTaskCompletion Completion(SubagentTaskRecord task, SubagentTaskStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(task.ChildTurnId, task.ChildConversationId, MessageRole.Assistant, string.Empty,
            status == SubagentTaskStatus.Cancelled ? MessageStatus.Cancelled : MessageStatus.Failed, now, now);
        return new SubagentTaskCompletion(status, new TurnFinalization(message, []), null, "Fixture", "Fixture failure", now);
    }

    private static SubagentTaskCreation CloneCreation(SubagentTaskRecord template)
    {
        var now = DateTimeOffset.UtcNow;
        var task = template with
        {
            Id = Guid.NewGuid(), ChildConversationId = Guid.NewGuid(), ChildTurnId = Guid.NewGuid(), ParentTurnId = Guid.NewGuid(),
            CreatedAtUtc = now, QueuedAtUtc = now, UpdatedAtUtc = now
        };
        var child = new ConversationRecord(task.ChildConversationId, "Child", null, ConversationMode.Programming,
            ToolPermissionMode.RequireApproval, task.SubagentId, now, now, Kind: ConversationKind.Subagent, ParentConversationId: task.ParentConversationId);
        return new SubagentTaskCreation(child,
            new MessageRecord(Guid.NewGuid(), child.Id, MessageRole.User, task.TaskText, MessageStatus.Completed, now, now), task);
    }

    private static async Task WaitForCountAsync(ConcurrentQueue<SubagentStateChange> changes, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (changes.Count < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
