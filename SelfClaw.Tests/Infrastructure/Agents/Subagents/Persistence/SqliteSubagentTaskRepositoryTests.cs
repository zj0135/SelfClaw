using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Agents.Subagents.Persistence;

public sealed class SqliteSubagentTaskRepositoryTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAsync_persists_queued_task_without_turn_or_message()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var creation = CreateTaskCreation(parent, Guid.NewGuid(), "Review the current change.");

        var created = await context.Tasks.CreateAsync(creation);

        created.Should().Be(creation.Task);
        (await context.Tasks.GetAsync(parent.Id, creation.Task.Id)).Should().Be(creation.Task);
        (await context.Tasks.ListAsync(parent.Id)).Should().Equal(creation.Task);
        (await context.Conversations.ListConversationsAsync()).Should().Equal(parent);
        (await context.Conversations.GetConversationAsync(creation.ChildConversation.Id))
            .Should().Be(creation.ChildConversation);
        (await context.Conversations.ListMessagesAsync(creation.ChildConversation.Id))
            .Should().BeEmpty();
        (await context.Turns.ListTurnsAsync(creation.ChildConversation.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Queries_are_scoped_to_the_parent_conversation()
    {
        var context = await CreateContextAsync();
        var owner = CreateParentConversation();
        var otherParent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(owner);
        await context.Conversations.UpsertConversationAsync(otherParent);
        var creation = CreateTaskCreation(owner, Guid.NewGuid(), "Inspect ownership.");
        await context.Tasks.CreateAsync(creation);

        (await context.Tasks.GetAsync(otherParent.Id, creation.Task.Id)).Should().BeNull();
        (await context.Tasks.ListAsync(otherParent.Id)).Should().BeEmpty();
        (await context.Tasks.GetDeliveryAsync(otherParent.Id, creation.Task.Id)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_rolls_back_child_when_task_insert_fails()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var creation = CreateTaskCreation(parent, Guid.NewGuid(), "Invalid timeout.");
        creation = creation with { Task = creation.Task with { MaxRunSeconds = 29 } };

        var action = () => context.Tasks.CreateAsync(creation);

        await action.Should().ThrowAsync<SqliteException>();
        (await context.Conversations.GetConversationAsync(creation.ChildConversation.Id)).Should().BeNull();
        (await context.Conversations.ListMessagesAsync(creation.ChildConversation.Id)).Should().BeEmpty();
        (await context.Tasks.GetAsync(parent.Id, creation.Task.Id)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_rejects_child_conversation_with_wrong_parent()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var creation = CreateTaskCreation(parent, Guid.NewGuid(), "Original task.");
        creation = creation with
        {
            ChildConversation = creation.ChildConversation with { ParentConversationId = Guid.NewGuid() }
        };

        var action = () => context.Tasks.CreateAsync(creation);

        await action.Should().ThrowAsync<ArgumentException>();
        (await context.Conversations.GetConversationAsync(creation.ChildConversation.Id)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_rejects_a_ninth_task_for_the_same_parent_turn_without_partial_rows()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        var parentTurnId = Guid.NewGuid();
        await context.Conversations.UpsertConversationAsync(parent);
        for (var index = 0; index < 8; index++)
        {
            await context.Tasks.CreateAsync(CreateTaskCreation(parent, parentTurnId, $"Task {index}"));
        }

        var rejected = CreateTaskCreation(parent, parentTurnId, "Task 9");
        var action = () => context.Tasks.CreateAsync(rejected);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*more than 8*");
        (await context.Tasks.ListAsync(parent.Id)).Should().HaveCount(8);
        (await context.Conversations.GetConversationAsync(rejected.ChildConversation.Id)).Should().BeNull();
        (await context.Conversations.ListMessagesAsync(rejected.ChildConversation.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_the_parent_cascades_child_task_message_and_delivery()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var creation = CreateTaskCreation(parent, Guid.NewGuid(), "Review deletion.");
        await context.Tasks.CreateAsync(creation);
        await InsertDeliveryAsync(context.Database, creation.Task);

        await context.Conversations.DeleteConversationAsync(parent.Id);

        await using var connection = await context.Database.OpenConnectionAsync();
        (await CountAsync(connection, "conversations")).Should().Be(0);
        (await CountAsync(connection, "messages")).Should().Be(0);
        (await CountAsync(connection, "subagent_tasks")).Should().Be(0);
        (await CountAsync(connection, "subagent_deliveries")).Should().Be(0);
    }

    [Theory]
    [InlineData(ConversationKind.Interactive, true)]
    [InlineData(ConversationKind.Subagent, false)]
    [InlineData((ConversationKind)99, false)]
    public async Task Conversation_repository_rejects_invalid_ownership_shapes(
        ConversationKind kind,
        bool includeParent)
    {
        var context = await CreateContextAsync();
        var conversation = CreateParentConversation() with
        {
            Kind = kind,
            ParentConversationId = includeParent ? Guid.NewGuid() : null
        };

        var action = () => context.Conversations.UpsertConversationAsync(conversation);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryClaimNextAsync_enforces_fifo_global_and_parent_limits()
    {
        var context = await CreateContextAsync();
        var firstParent = CreateParentConversation();
        var secondParent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(firstParent);
        await context.Conversations.UpsertConversationAsync(secondParent);
        var firstParentTasks = new List<SubagentTaskRecord>();
        for (var index = 0; index < 5; index++)
        {
            firstParentTasks.Add(await context.Tasks.CreateAsync(
                CreateTaskCreation(firstParent, Guid.NewGuid(), $"First {index}")));
        }

        var secondParentTask = await context.Tasks.CreateAsync(
            CreateTaskCreation(secondParent, Guid.NewGuid(), "Second"));

        var firstClaim = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);
        var secondClaim = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);
        var thirdClaim = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);
        var fourthClaim = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);
        var blocked = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);

        new[] { firstClaim!.Id, secondClaim!.Id, thirdClaim!.Id }
            .Should().Equal(firstParentTasks.Take(3).Select(task => task.Id));
        fourthClaim!.Id.Should().Be(secondParentTask.Id);
        blocked.Should().BeNull("the global running limit is four");
    }

    [Fact]
    public async Task TryCompleteAsync_atomically_finalizes_child_task_and_pending_delivery()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var queued = await context.Tasks.CreateAsync(
            CreateTaskCreation(parent, Guid.NewGuid(), "Review completion."));
        var running = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);
        var completion = await CreateCompletionAsync(context,
            running!,
            SubagentTaskStatus.Succeeded,
            "pure provider final",
            "<thinking>private</thinking>pure provider final");

        var terminal = await context.Tasks.TryCompleteAsync(
            running!.Id,
            SubagentTaskStatus.Running,
            completion);

        terminal.Should().NotBeNull();
        terminal!.Status.Should().Be(SubagentTaskStatus.Succeeded);
        terminal.FinalText.Should().Be("pure provider final");
        (await context.Conversations.ListMessagesAsync(queued.ChildConversationId))
            .Single(message => message.Role == MessageRole.Assistant)
            .MarkdownContent.Should().Contain("private");
        var delivery = await context.Tasks.GetDeliveryAsync(parent.Id, queued.Id);
        delivery.Should().NotBeNull();
        delivery!.Status.Should().Be(SubagentDeliveryStatus.Pending);
        delivery.EnvelopeJson.Should().Contain("pure provider final").And.NotContain("private");
        (await context.Tasks.TryCompleteAsync(
            running.Id,
            SubagentTaskStatus.Running,
            completion)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_can_atomically_accept_an_initial_failed_task()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var creation = CreateTaskCreation(parent, Guid.NewGuid(), "Missing definition.");
        creation = creation with
        {
            InitialCompletion = await CreateCompletionAsync(context,
                creation.Task,
                SubagentTaskStatus.Failed,
                finalText: null,
                assistantMarkdown: string.Empty,
                "DefinitionMissing")
        };

        var created = await context.Tasks.CreateAsync(creation);

        created.Status.Should().Be(SubagentTaskStatus.Failed);
        created.ErrorCode.Should().Be("DefinitionMissing");
        (await context.Tasks.GetDeliveryAsync(parent.Id, created.Id)).Should().NotBeNull();
        (await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_rejects_retry_that_changes_the_frozen_snapshot()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var original = await context.Tasks.CreateAsync(
            CreateTaskCreation(parent, Guid.NewGuid(), "Original." ) with
            {
                InitialCompletion = null
            });
        var claimed = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);
        _ = await context.Tasks.TryCompleteAsync(
            claimed!.Id,
            SubagentTaskStatus.Running,
            await CreateCompletionAsync(context, claimed, SubagentTaskStatus.Succeeded, "done", "done"));
        var retry = CreateTaskCreation(parent, Guid.NewGuid(), original.TaskText);
        retry = retry with
        {
            Task = retry.Task with
            {
                Attempt = 2,
                RetryOfTaskId = original.Id,
                DefinitionSnapshotJson = "{\"changed\":true}"
            }
        };

        var action = () => context.Tasks.CreateAsync(retry);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*must copy*");
    }

    [Fact]
    public async Task RequestCancellationAsync_marks_only_an_owned_running_task()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        var other = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        await context.Conversations.UpsertConversationAsync(other);
        var task = await context.Tasks.CreateAsync(CreateTaskCreation(parent, Guid.NewGuid(), "Cancel me."));
        _ = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow);
        var requestedAt = DateTimeOffset.UtcNow;

        (await context.Tasks.RequestCancellationAsync(other.Id, task.Id, requestedAt)).Should().BeNull();
        var requested = await context.Tasks.RequestCancellationAsync(parent.Id, task.Id, requestedAt);

        requested.Should().NotBeNull();
        requested!.Status.Should().Be(SubagentTaskStatus.Running);
        requested.CancelRequestedAtUtc.Should().Be(requestedAt);
    }

    [Fact]
    public async Task Delivery_lease_batches_fifo_within_the_exact_utf8_limit_and_snapshot()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        var parentTurnId = Guid.NewGuid();
        await context.Conversations.UpsertConversationAsync(parent);
        var first = await CompleteTaskAsync(context, CreateTaskCreation(parent, parentTurnId, "First"), new string('a', 24_000));
        var second = await CompleteTaskAsync(context, CreateTaskCreation(parent, parentTurnId, "Second"), new string('b', 24_000));
        _ = await CompleteTaskAsync(context, CreateTaskCreation(parent, parentTurnId, "Third"), new string('c', 24_000));
        var differentSnapshot = CreateTaskCreation(parent, parentTurnId, "Different snapshot");
        differentSnapshot = differentSnapshot with
        {
            Task = differentSnapshot.Task with { ParentExecutionSnapshotJson = "{\"snapshot\":2}" }
        };
        _ = await CompleteTaskAsync(context, differentSnapshot, "different");
        var now = DateTimeOffset.UtcNow.AddSeconds(3);
        var mailbox = await context.Deliveries.PeekReadyMailboxAsync(now, now);

        var lease = await context.Deliveries.TryLeaseBatchAsync(
            mailbox!,
            Guid.NewGuid(),
            Guid.NewGuid(),
            now,
            now.AddSeconds(45),
            64 * 1024);

        lease.Should().NotBeNull();
        lease!.Deliveries.Select(delivery => delivery.TaskId).Should().Equal(first.Id, second.Id);
        var exactBytes = System.Text.Encoding.UTF8.GetByteCount("{\"deliveries\":[" +
            string.Join(',', lease.Deliveries.Select(delivery => delivery.EnvelopeJson)) + "]}");
        exactBytes.Should().BeLessThanOrEqualTo(64 * 1024);
        lease.Deliveries.Should().OnlyContain(delivery => delivery.AttemptCount == 1);
    }

    [Fact]
    public async Task Delivery_success_atomically_commits_parent_turn_and_rejects_a_stale_lease()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var task = await CompleteTaskAsync(
            context,
            CreateTaskCreation(parent, Guid.NewGuid(), "Complete parent."),
            "child result");
        var now = DateTimeOffset.UtcNow.AddSeconds(3);
        var lease = await LeaseNextAsync(context, now);
        var turn = new ConversationTurnRecord(lease.ContinuationTurnId, parent.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Continuation, ConversationTurnStatus.Succeeded, now, now);
        var assistant = new MessageRecord(Guid.NewGuid(), parent.Id, turn.Id,
            await context.Turns.ReserveMessageSequenceAsync(parent.Id), MessageRole.Assistant,
            "parent continuation", MessageStatus.Sealed, now, now);
        (await context.Turns.ListTurnsAsync(parent.Id)).Should().BeEmpty();
        (await context.Conversations.ListMessagesAsync(parent.Id)).Should().BeEmpty();
        var resolution = new SubagentDeliveryResolution(SubagentDeliveryResolutionKind.Succeeded,
            new ConversationTurnCommit(turn, [assistant], []), null, now);

        var committed = await context.Deliveries.TryResolveAsync(lease, resolution);
        var repeated = await context.Deliveries.TryResolveAsync(lease, resolution);

        committed.LeaseMatched.Should().BeTrue();
        committed.DeliveredDeliveryIds.Should().Equal(lease.Deliveries[0].Id);
        repeated.LeaseMatched.Should().BeFalse();
        (await context.Deliveries.GetAsync(parent.Id, task.Id))!.Status
            .Should().Be(SubagentDeliveryStatus.Delivered);
        (await context.Conversations.ListMessagesAsync(parent.Id))
            .Should().ContainSingle().Which.Should().BeEquivalentTo(assistant);
    }

    [Fact]
    public async Task Delivery_retry_uses_ten_and_thirty_second_backoff_then_dead_letters_without_parent_message()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var task = await CompleteTaskAsync(
            context,
            CreateTaskCreation(parent, Guid.NewGuid(), "Retry continuation."),
            "child result");
        var firstAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var firstLease = await LeaseNextAsync(context, firstAt);

        var first = await context.Deliveries.TryResolveAsync(
            firstLease,
            RetryableFailure(firstAt, "first failure"));
        var firstPending = await context.Deliveries.GetAsync(parent.Id, task.Id);
        var secondAt = firstAt.AddSeconds(11);
        var secondLease = await LeaseNextAsync(context, secondAt);
        var second = await context.Deliveries.TryResolveAsync(
            secondLease,
            RetryableFailure(secondAt, "second failure"));
        var secondPending = await context.Deliveries.GetAsync(parent.Id, task.Id);
        var thirdAt = secondAt.AddSeconds(31);
        var thirdLease = await LeaseNextAsync(context, thirdAt);
        var third = await context.Deliveries.TryResolveAsync(
            thirdLease,
            RetryableFailure(thirdAt, "third failure"));

        first.PendingDeliveryIds.Should().ContainSingle();
        firstPending!.NextAttemptAtUtc.Should().Be(firstAt.AddSeconds(10));
        second.PendingDeliveryIds.Should().ContainSingle();
        secondPending!.NextAttemptAtUtc.Should().Be(secondAt.AddSeconds(30));
        third.DeadLetteredDeliveryIds.Should().ContainSingle();
        (await context.Deliveries.GetAsync(parent.Id, task.Id))!.Status
            .Should().Be(SubagentDeliveryStatus.DeadLetter);
        (await context.Conversations.ListMessagesAsync(parent.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delivery_unsafe_failure_atomically_persists_failed_parent_turn_and_dead_letter()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var task = await CompleteTaskAsync(
            context,
            CreateTaskCreation(parent, Guid.NewGuid(), "Unsafe continuation."),
            "child result");
        var now = DateTimeOffset.UtcNow.AddSeconds(3);
        var lease = await LeaseNextAsync(context, now);
        const string error = "provider failed after a tool call";
        var turn = new ConversationTurnRecord(lease.ContinuationTurnId, parent.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Continuation, ConversationTurnStatus.Failed, now, now, error);
        var assistantId = Guid.NewGuid();
        var tool = new ToolExecutionRecord(Guid.NewGuid(), parent.Id, "write_file", "{}",
            ToolExecutionStatus.Failed, "provider failed", "call-1", 10, now, now, MessageId: assistantId);
        var assistant = new MessageRecord(assistantId, parent.Id, turn.Id,
            await context.Turns.ReserveMessageSequenceAsync(parent.Id), MessageRole.Assistant,
            "partial", MessageStatus.Interrupted, now, now,
            Segments: [new MessageSegmentRecord(assistantId, 0, MessageSegmentKind.ToolCall, null, tool.Id)]);

        var resolved = await context.Deliveries.TryResolveAsync(
            lease,
            new SubagentDeliveryResolution(
                SubagentDeliveryResolutionKind.UnsafeFailure,
                new ConversationTurnCommit(turn, [assistant], [tool]),
                error,
                now));

        resolved.DeadLetteredDeliveryIds.Should().ContainSingle();
        (await context.Deliveries.GetAsync(parent.Id, task.Id))!.Status
            .Should().Be(SubagentDeliveryStatus.DeadLetter);
        (await context.Conversations.ListMessagesAsync(parent.Id)).Should().ContainSingle().Which.Should().BeEquivalentTo(assistant);
        (await context.Conversations.ListToolExecutionsAsync(parent.Id)).Should().ContainSingle().Which.Should().Be(tool);
    }

    [Fact]
    public async Task Delivery_lease_renewal_is_atomic_and_rejects_a_mismatched_batch()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        var parentTurnId = Guid.NewGuid();
        await context.Conversations.UpsertConversationAsync(parent);
        _ = await CompleteTaskAsync(context, CreateTaskCreation(parent, parentTurnId, "First"), "first");
        _ = await CompleteTaskAsync(context, CreateTaskCreation(parent, parentTurnId, "Second"), "second");
        var leasedAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var lease = await LeaseNextAsync(context, leasedAt);
        lease.Deliveries.Should().HaveCount(2);
        var firstRenewal = leasedAt.AddSeconds(15);

        (await context.Deliveries.TryRenewLeaseAsync(
            lease,
            firstRenewal,
            firstRenewal.AddSeconds(45))).Should().BeTrue();

        var mismatched = lease with
        {
            Deliveries =
            [
                lease.Deliveries[0],
                lease.Deliveries[1] with { Id = Guid.NewGuid() }
            ]
        };
        var rejectedRenewal = firstRenewal.AddSeconds(15);
        (await context.Deliveries.TryRenewLeaseAsync(
            mismatched,
            rejectedRenewal,
            rejectedRenewal.AddSeconds(45))).Should().BeFalse();

        foreach (var delivery in lease.Deliveries)
        {
            (await context.Deliveries.GetAsync(parent.Id, delivery.TaskId))!.LeasedUntilUtc
                .Should().Be(firstRenewal.AddSeconds(45));
        }
    }

    [Fact]
    public async Task Delivery_expired_lease_retries_without_tools_and_dead_letters_a_recorded_tool_turn()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var task = await CompleteTaskAsync(
            context,
            CreateTaskCreation(parent, Guid.NewGuid(), "Recover continuation."),
            "child result");
        var firstAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var firstLease = await LeaseNextAsync(context, firstAt);

        (await context.Deliveries.RecoverExpiredLeasesAsync(firstAt.AddSeconds(46)))
            .Should().BeEmpty();
        (await context.Deliveries.GetAsync(parent.Id, task.Id))!.Status
            .Should().Be(SubagentDeliveryStatus.Pending);

        var secondAt = firstAt.AddSeconds(47);
        var secondLease = await LeaseNextAsync(context, secondAt);
        (await context.Deliveries.TryMarkToolExecutionStartedAsync(secondLease, secondAt)).Should().BeTrue();
        (await context.Turns.ListTurnsAsync(parent.Id)).Should().BeEmpty();
        (await context.Conversations.ListMessagesAsync(parent.Id)).Should().BeEmpty();

        var deadLetters = await context.Deliveries.RecoverExpiredLeasesAsync(secondAt.AddSeconds(46));

        deadLetters.Should().ContainSingle().Which.Id.Should().Be(secondLease.Deliveries[0].Id);
        (await context.Deliveries.GetAsync(parent.Id, task.Id))!.Status
            .Should().Be(SubagentDeliveryStatus.DeadLetter);
        (await context.Conversations.ListMessagesAsync(parent.Id)).Should().BeEmpty();
        (await context.Conversations.ListToolExecutionsAsync(parent.Id)).Should().BeEmpty();
        (await context.Turns.ListTurnsAsync(parent.Id)).Should().ContainSingle(turn =>
            turn.Id == secondLease.ContinuationTurnId && turn.Status == ConversationTurnStatus.Interrupted);
    }

    [Fact]
    public async Task Delivery_competing_leases_have_a_single_winner_per_parent()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        _ = await CompleteTaskAsync(
            context,
            CreateTaskCreation(parent, Guid.NewGuid(), "Compete for mailbox."),
            "child result");
        var now = DateTimeOffset.UtcNow.AddSeconds(3);
        var mailbox = await context.Deliveries.PeekReadyMailboxAsync(now, now);

        var attempts = Enumerable.Range(0, 8).Select(_ => context.Deliveries.TryLeaseBatchAsync(
            mailbox!,
            Guid.NewGuid(),
            Guid.NewGuid(),
            now,
            now.AddSeconds(45),
            64 * 1024));
        var leases = await Task.WhenAll(attempts);

        leases.Should().ContainSingle(candidate => candidate != null);
    }

    [Fact]
    public async Task Claim_failure_rolls_back_running_task_turn_user_and_sequence()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var task = await context.Tasks.CreateAsync(CreateTaskCreation(parent, Guid.NewGuid(), "claim"));
        await ExecuteAsync(context.Database, "CREATE TRIGGER fail_child_user BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT, 'claim fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow));
        (await context.Tasks.GetAsync(parent.Id, task.Id))?.Status.Should().Be(SubagentTaskStatus.Queued);
        (await context.Turns.ListTurnsAsync(task.ChildConversationId)).Should().BeEmpty();
        (await context.Conversations.ListMessagesAsync(task.ChildConversationId)).Should().BeEmpty();
        await using var connection = await context.Database.OpenConnectionAsync();
        (await CountAsync(connection, "conversation_message_sequences")).Should().Be(0);
    }

    [Fact]
    public async Task Queued_cancellation_atomically_persists_terminal_turn_and_user_without_assistant()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var task = await context.Tasks.CreateAsync(CreateTaskCreation(parent, Guid.NewGuid(), "cancel queued"));
        var completion = await CreateCompletionAsync(context, task, SubagentTaskStatus.Cancelled, null, string.Empty);
        await ExecuteAsync(context.Database, "CREATE TRIGGER fail_delivery BEFORE INSERT ON subagent_deliveries BEGIN SELECT RAISE(ABORT, 'completion fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => context.Tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Queued, completion));
        (await context.Turns.ListTurnsAsync(task.ChildConversationId)).Should().BeEmpty();
        (await context.Conversations.ListMessagesAsync(task.ChildConversationId)).Should().BeEmpty();
        (await context.Tasks.GetAsync(parent.Id, task.Id))?.Status.Should().Be(SubagentTaskStatus.Queued);
        await ExecuteAsync(context.Database, "DROP TRIGGER fail_delivery;");
        await context.Tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Queued, completion);
        (await context.Turns.ListTurnsAsync(task.ChildConversationId)).Should().ContainSingle().Which.Status.Should().Be(ConversationTurnStatus.Cancelled);
        var user = (await context.Conversations.ListMessagesAsync(task.ChildConversationId)).Should().ContainSingle().Subject;
        user.Role.Should().Be(MessageRole.User);
        user.MarkdownContent.Should().Be(task.TaskText);
        user.Id.Should().NotBe(task.ChildTurnId);
        (await context.Tasks.GetDeliveryAsync(parent.Id, task.Id)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(ConversationTurnStatus.Blocked)]
    [InlineData(ConversationTurnStatus.Truncated)]
    public async Task Child_failed_policy_preserves_actual_turn_outcome(ConversationTurnStatus status)
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var queued = await context.Tasks.CreateAsync(CreateTaskCreation(parent, Guid.NewGuid(), "outcome"));
        var running = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("Missing child.");
        var turn = (await context.Turns.ListTurnsAsync(queued.ChildConversationId)).Single();
        await context.Tasks.TryCompleteAsync(running.Id, SubagentTaskStatus.Running, new SubagentTaskCompletion(
            SubagentTaskStatus.Failed, new ConversationTurnCommit(turn with
            {
                Status = status, CompletedAtUtc = DateTimeOffset.UtcNow
            }, [], []), null, "policy", "stopped", DateTimeOffset.UtcNow));
        (await context.Turns.ListTurnsAsync(queued.ChildConversationId)).Single().Status.Should().Be(status);
    }

    [Fact]
    public async Task Delivery_terminal_fault_rolls_back_delivery_turn_fragment_and_usage()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var task = await CompleteTaskAsync(context, CreateTaskCreation(parent, Guid.NewGuid(), "delivery rollback"), "child");
        var now = DateTimeOffset.UtcNow.AddSeconds(3);
        var lease = await LeaseNextAsync(context, now);
        var turn = new ConversationTurnRecord(lease.ContinuationTurnId, parent.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Continuation, ConversationTurnStatus.Succeeded, now, now, Usage: new TurnUsage(InputTokens: 7, TotalTokens: null));
        var message = new MessageRecord(Guid.NewGuid(), parent.Id, turn.Id,
            await context.Turns.ReserveMessageSequenceAsync(parent.Id), MessageRole.Assistant, "answer", MessageStatus.Sealed, now, now);
        var resolution = new SubagentDeliveryResolution(SubagentDeliveryResolutionKind.Succeeded, new(turn, [message], []), null, now);
        await ExecuteAsync(context.Database, "CREATE TRIGGER fail_parent_usage BEFORE INSERT ON turn_usage BEGIN SELECT RAISE(ABORT, 'delivery fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => context.Deliveries.TryResolveAsync(lease, resolution));
        (await context.Turns.ListTurnsAsync(parent.Id)).Should().BeEmpty();
        (await context.Conversations.ListMessagesAsync(parent.Id)).Should().BeEmpty();
        (await context.Deliveries.GetAsync(parent.Id, task.Id))?.Status.Should().Be(SubagentDeliveryStatus.Leased);
        await ExecuteAsync(context.Database, "DROP TRIGGER fail_parent_usage;");
        (await context.Deliveries.TryResolveAsync(lease, resolution)).LeaseMatched.Should().BeTrue();
        (await context.Turns.ListTurnsAsync(parent.Id)).Single().Usage?.TotalTokens.Should().BeNull();
    }

    private static async Task ExecuteAsync(SqliteDatabase database, string sql)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<SubagentPersistenceTestContext> CreateContextAsync()
    {
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        var deliveries = new SqliteSubagentDeliveryRepository(database);
        await tasks.InitializeAsync();
        return new SubagentPersistenceTestContext(database, conversations, new SqliteConversationTurnRepository(database), tasks, deliveries);
    }

    private static ConversationRecord CreateParentConversation()
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationRecord(
            Guid.NewGuid(),
            "Parent",
            WorkspaceRootId: null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            AgentId: "build",
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
    }

    private static SubagentTaskCreation CreateTaskCreation(
        ConversationRecord parent,
        Guid parentTurnId,
        string taskText)
    {
        var now = DateTimeOffset.UtcNow;
        var childId = Guid.NewGuid();
        var child = new ConversationRecord(
            childId,
            "Subagent: Reviewer",
            parent.WorkspaceRootId,
            parent.Mode,
            parent.ToolPermissionMode,
            parent.AgentId,
            now,
            now,
            Kind: ConversationKind.Subagent,
            ParentConversationId: parent.Id);
        var task = new SubagentTaskRecord(
            Guid.NewGuid(),
            parent.Id,
            parentTurnId,
            childId,
            Guid.NewGuid(),
            "reviewer",
            "Reviewer",
            taskText,
            SubagentTaskStatus.Queued,
            Attempt: 1,
            RetryOfTaskId: null,
            DefinitionSnapshotJson: "{}",
            ParentExecutionSnapshotJson: "{}",
            ResolvedModelProfileId: null,
            MaxRunSeconds: 900,
            FinalText: null,
            InputTokens: null,
            OutputTokens: null,
            ErrorCode: null,
            ErrorMessage: null,
            CancelRequestedAtUtc: null,
            QueuedAtUtc: now,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
        return new SubagentTaskCreation(child, task);
    }

    private static async Task<SubagentTaskCompletion> CreateCompletionAsync(
        SubagentPersistenceTestContext context,
        SubagentTaskRecord task,
        SubagentTaskStatus status,
        string? finalText,
        string assistantMarkdown,
        string? errorCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        var turnStatus = status switch
        {
            SubagentTaskStatus.Succeeded => ConversationTurnStatus.Succeeded,
            SubagentTaskStatus.Cancelled => ConversationTurnStatus.Cancelled,
            SubagentTaskStatus.Interrupted => ConversationTurnStatus.Interrupted,
            _ => ConversationTurnStatus.Failed
        };
        var turn = new ConversationTurnRecord(task.ChildTurnId, task.ChildConversationId, AgentExecutionMode.Direct,
            DirectTurnOrigin.Subagent, turnStatus, task.StartedAtUtc ?? task.QueuedAtUtc, now, errorCode,
            new TurnUsage(InputTokens: 5, OutputTokens: 3));
        var messages = new List<MessageRecord>();
        if (task.Status == SubagentTaskStatus.Running && !string.IsNullOrEmpty(assistantMarkdown))
        {
            var id = Guid.NewGuid();
            messages.Add(new MessageRecord(id, task.ChildConversationId, task.ChildTurnId,
                await context.Turns.ReserveMessageSequenceAsync(task.ChildConversationId), MessageRole.Assistant,
                assistantMarkdown, status == SubagentTaskStatus.Succeeded ? MessageStatus.Sealed : MessageStatus.Interrupted,
                now, now, Segments: [new MessageSegmentRecord(id, 0, MessageSegmentKind.Text, assistantMarkdown, null)]));
        }

        return new SubagentTaskCompletion(status, new ConversationTurnCommit(turn, messages, []),
            finalText, errorCode, errorCode, now);
    }

    private static async Task InsertDeliveryAsync(SqliteDatabase database, SubagentTaskRecord task)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO subagent_deliveries(
                id, task_id, parent_conversation_id, parent_turn_id, status,
                envelope_json, envelope_bytes, attempt_count, next_attempt_at_utc,
                created_at_utc, updated_at_utc)
            VALUES(
                $id, $taskId, $parentConversationId, $parentTurnId, 0,
                '{}', 2, 0, $nextAttemptAt, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
        command.Parameters.AddWithValue("$parentConversationId", task.ParentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$parentTurnId", task.ParentTurnId.ToString("D"));
        command.Parameters.AddWithValue("$nextAttemptAt", now.ToString("O"));
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SubagentTaskRecord> CompleteTaskAsync(
        SubagentPersistenceTestContext context,
        SubagentTaskCreation creation,
        string finalText)
    {
        await context.Tasks.CreateAsync(creation);
        var running = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("The fixture task could not be claimed.");
        return await context.Tasks.TryCompleteAsync(
            running.Id,
            SubagentTaskStatus.Running,
            await CreateCompletionAsync(context, running, SubagentTaskStatus.Succeeded, finalText, finalText))
            ?? throw new InvalidOperationException("The fixture task could not be completed.");
    }

    private static async Task<SubagentDeliveryLease> LeaseNextAsync(SubagentPersistenceTestContext context, DateTimeOffset now)
    {
        var mailbox = await context.Deliveries.PeekReadyMailboxAsync(now, now)
            ?? throw new InvalidOperationException("The fixture mailbox is not ready.");
        return await context.Deliveries.TryLeaseBatchAsync(
            mailbox,
            Guid.NewGuid(),
            Guid.NewGuid(),
            now,
            now.AddSeconds(45),
            64 * 1024)
            ?? throw new InvalidOperationException("The fixture mailbox could not be leased.");
    }

    private static SubagentDeliveryResolution RetryableFailure(DateTimeOffset occurredAtUtc, string error)
        => new(SubagentDeliveryResolutionKind.RetryableFailure, null, error, occurredAtUtc);

    private static async Task<long> CountAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

}
