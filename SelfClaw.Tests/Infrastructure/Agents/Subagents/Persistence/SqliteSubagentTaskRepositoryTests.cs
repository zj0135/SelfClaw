using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
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
    public async Task CreateAsync_atomically_persists_child_message_and_queued_task()
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
            .Should().Equal(creation.TaskMessage);
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
    public async Task CreateAsync_rolls_back_child_and_message_when_task_insert_fails()
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
    public async Task CreateAsync_rejects_task_text_that_differs_from_the_child_message()
    {
        var context = await CreateContextAsync();
        var parent = CreateParentConversation();
        await context.Conversations.UpsertConversationAsync(parent);
        var creation = CreateTaskCreation(parent, Guid.NewGuid(), "Original task.");
        creation = creation with
        {
            TaskMessage = creation.TaskMessage with { MarkdownContent = "Different task." }
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
        var completion = CreateCompletion(
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
            InitialCompletion = CreateCompletion(
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
            CreateCompletion(claimed, SubagentTaskStatus.Succeeded, "done", "done"));
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
        var assistant = new MessageRecord(
            lease.ContinuationTurnId,
            parent.Id,
            MessageRole.Assistant,
            "parent continuation",
            MessageStatus.Completed,
            now,
            now);
        var resolution = new SubagentDeliveryResolution(
            SubagentDeliveryResolutionKind.Succeeded,
            new TurnFinalization(assistant, []),
            Error: null,
            now);

        var committed = await context.Deliveries.TryResolveAsync(lease, resolution);
        var repeated = await context.Deliveries.TryResolveAsync(lease, resolution);

        committed.LeaseMatched.Should().BeTrue();
        committed.DeliveredDeliveryIds.Should().Equal(lease.Deliveries[0].Id);
        repeated.LeaseMatched.Should().BeFalse();
        (await context.Deliveries.GetAsync(parent.Id, task.Id))!.Status
            .Should().Be(SubagentDeliveryStatus.Delivered);
        (await context.Conversations.ListMessagesAsync(parent.Id))
            .Should().ContainSingle().Which.Should().Be(assistant);
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
        var assistant = new MessageRecord(
            lease.ContinuationTurnId,
            parent.Id,
            MessageRole.Assistant,
            "partial",
            MessageStatus.Failed,
            now,
            now,
            ErrorMessage: "provider failed after a tool call");
        var tool = new ToolExecutionRecord(
            Guid.NewGuid(),
            parent.Id,
            "write_file",
            "{}",
            ToolExecutionStatus.Failed,
            "provider failed",
            "call-1",
            10,
            now,
            now,
            MessageId: lease.ContinuationTurnId);

        var resolved = await context.Deliveries.TryResolveAsync(
            lease,
            new SubagentDeliveryResolution(
                SubagentDeliveryResolutionKind.UnsafeFailure,
                new TurnFinalization(assistant, [tool]),
                assistant.ErrorMessage,
                now));

        resolved.DeadLetteredDeliveryIds.Should().ContainSingle();
        (await context.Deliveries.GetAsync(parent.Id, task.Id))!.Status
            .Should().Be(SubagentDeliveryStatus.DeadLetter);
        (await context.Conversations.ListMessagesAsync(parent.Id)).Should().ContainSingle().Which.Should().Be(assistant);
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
        var tool = new ToolExecutionRecord(
            Guid.NewGuid(),
            parent.Id,
            "write_file",
            "{}",
            ToolExecutionStatus.Running,
            ResultSummary: null,
            "call-recovery",
            DurationMs: null,
            secondAt,
            secondAt,
            MessageId: secondLease.ContinuationTurnId);
        await context.Conversations.UpsertToolExecutionAsync(tool);

        var deadLetters = await context.Deliveries.RecoverExpiredLeasesAsync(secondAt.AddSeconds(46));

        deadLetters.Should().ContainSingle().Which.Id.Should().Be(secondLease.Deliveries[0].Id);
        (await context.Deliveries.GetAsync(parent.Id, task.Id))!.Status
            .Should().Be(SubagentDeliveryStatus.DeadLetter);
        (await context.Conversations.ListMessagesAsync(parent.Id)).Should().ContainSingle(message =>
            message.Id == secondLease.ContinuationTurnId && message.Status == MessageStatus.Failed);
        (await context.Conversations.ListToolExecutionsAsync(parent.Id)).Should().ContainSingle(record =>
            record.Id == tool.Id && record.Status == ToolExecutionStatus.Failed);
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

    private async Task<TestContext> CreateContextAsync()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        var deliveries = new SqliteSubagentDeliveryRepository(database);
        await tasks.InitializeAsync();
        return new TestContext(database, conversations, tasks, deliveries);
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
        var message = new MessageRecord(
            Guid.NewGuid(),
            childId,
            MessageRole.User,
            taskText,
            MessageStatus.Completed,
            now,
            now);
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
        return new SubagentTaskCreation(child, message, task);
    }

    private static SubagentTaskCompletion CreateCompletion(
        SubagentTaskRecord task,
        SubagentTaskStatus status,
        string? finalText,
        string assistantMarkdown,
        string? errorCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        var messageStatus = status switch
        {
            SubagentTaskStatus.Succeeded => MessageStatus.Completed,
            SubagentTaskStatus.Cancelled => MessageStatus.Cancelled,
            _ => MessageStatus.Failed
        };
        var assistant = new MessageRecord(
            task.ChildTurnId,
            task.ChildConversationId,
            MessageRole.Assistant,
            assistantMarkdown,
            messageStatus,
            now,
            now,
            InputTokens: 5,
            OutputTokens: 3,
            ErrorMessage: errorCode);
        return new SubagentTaskCompletion(
            status,
            new TurnFinalization(assistant, []),
            finalText,
            errorCode,
            errorCode,
            now);
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
        TestContext context,
        SubagentTaskCreation creation,
        string finalText)
    {
        await context.Tasks.CreateAsync(creation);
        var running = await context.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("The fixture task could not be claimed.");
        return await context.Tasks.TryCompleteAsync(
            running.Id,
            SubagentTaskStatus.Running,
            CreateCompletion(running, SubagentTaskStatus.Succeeded, finalText, finalText))
            ?? throw new InvalidOperationException("The fixture task could not be completed.");
    }

    private static async Task<SubagentDeliveryLease> LeaseNextAsync(TestContext context, DateTimeOffset now)
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

    private sealed record TestContext(
        SqliteDatabase Database,
        SqliteConversationRepository Conversations,
        SqliteSubagentTaskRepository Tasks,
        SqliteSubagentDeliveryRepository Deliveries);
}
