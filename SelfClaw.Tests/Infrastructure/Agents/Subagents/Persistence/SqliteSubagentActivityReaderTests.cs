using SelfClaw.Core.Runtime;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Subagents.Persistence;

public sealed class SqliteSubagentActivityReaderTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly SqliteSubagentTaskRepository _tasks;
    private readonly SqliteSubagentActivityReader _reader;

    public SqliteSubagentActivityReaderTests()
    {
        _database = new SqliteDatabase(new StoragePaths(_rootPath, Path.Combine(_rootPath, "activity.db"), Path.Combine(_rootPath, "secrets")));
        _conversations = new SqliteConversationRepository(_database);
        _tasks = new SqliteSubagentTaskRepository(_database, new SubagentCompletionEnvelopeFactory());
        _reader = new SqliteSubagentActivityReader(_database);
    }

    [Fact]
    public async Task ListAsync_counts_the_whole_parent_and_pages_same_named_tasks_and_retries_in_stable_order()
    {
        var running = await CreateRunningAsync();
        var parent = await GetParentAsync(running);
        SubagentTaskRecord? failed = null;
        foreach (var status in new[] { SubagentTaskStatus.Succeeded, SubagentTaskStatus.Failed, SubagentTaskStatus.Cancelled, SubagentTaskStatus.Interrupted })
        {
            var task = await SubagentTaskTestData.CreateQueuedTaskAsync(_conversations, _tasks, parent);
            await CompleteAsync(task, status);
            if (status == SubagentTaskStatus.Failed)
            {
                failed = task;
            }
        }

        for (var index = 0; index < 55; index++)
        {
            await SubagentTaskTestData.CreateQueuedTaskAsync(_conversations, _tasks, parent);
        }

        var retry = await CreateRetryAsync(failed ?? throw new InvalidOperationException("Missing retry predecessor."), parent);
        var otherParent = await SubagentTaskTestData.CreateQueuedTaskAsync(_conversations, _tasks);
        var first = await _reader.ListAsync(new SubagentActivityQuery(parent.Id));
        var second = await _reader.ListAsync(new SubagentActivityQuery(parent.Id, Cursor: first.NextCursor));

        first.Counts.Should().Be(new SubagentActivityCounts(61, 56, 1, 1, 1, 1, 1));
        second.Counts.Should().Be(first.Counts);
        first.Tasks.Should().HaveCount(50).And.OnlyContain(task => task.Status == SubagentTaskStatus.Queued || task.Status == SubagentTaskStatus.Running);
        second.Tasks.Should().HaveCount(11);
        second.NextCursor.Should().BeNull();
        var tasks = first.Tasks.Concat(second.Tasks).ToArray();
        tasks.Select(task => task.TaskId).Should().OnlyHaveUniqueItems().And.NotContain(otherParent.Id);
        tasks.Should().OnlyContain(task => task.ParentConversationId == parent.Id && task.SubagentName == "Reviewer");
        tasks.Single(task => task.TaskId == retry.Id).Attempt.Should().Be(2);
        tasks.TakeLast(4).Should().OnlyContain(task => task.Status != SubagentTaskStatus.Queued && task.Status != SubagentTaskStatus.Running);
        (await _reader.ListAsync(new SubagentActivityQuery(parent.Id, running.ParentTurnId))).Counts.Total.Should().Be(1);
        (await _reader.ListAsync(new SubagentActivityQuery(running.ChildConversationId))).Counts.Total.Should().Be(0);
        (await _reader.GetDetailAsync(otherParent.ParentConversationId, running.Id)).Should().BeNull();
        var serialized = JsonSerializer.Serialize(first);
        serialized.Should().NotContain("SnapshotJson").And.NotContain("EnvelopeJson").And.NotContain("LeaseToken");
    }

    [Fact]
    public async Task ListAsync_invalidates_cursors_only_when_identity_or_sort_group_changes()
    {
        await _tasks.InitializeAsync();
        var queued = await SubagentTaskTestData.CreateQueuedTaskAsync(_conversations, _tasks);
        var parent = await GetParentAsync(queued);
        await SubagentTaskTestData.CreateQueuedTaskAsync(_conversations, _tasks, parent);
        var query = new SubagentActivityQuery(parent.Id, PageSize: 1);
        var initial = await _reader.ListAsync(query);
        var running = await _tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("Missing running task.");
        await _tasks.RequestCancellationAsync(parent.Id, running.Id, DateTimeOffset.UtcNow);
        var progress = await _reader.ListAsync(query with { Cursor = initial.NextCursor });
        progress.ListVersion.Should().Be(initial.ListVersion);
        progress.Counts.Running.Should().Be(1);
        await CompleteAsync(running, SubagentTaskStatus.Succeeded);
        var stale = () => _reader.ListAsync(query with { Cursor = initial.NextCursor });
        await stale.Should().ThrowAsync<SubagentActivityReadException>().WithMessage("task-list-changed");

        var completed = await _reader.ListAsync(query);
        var deliveries = new SqliteSubagentDeliveryRepository(_database);
        var now = DateTimeOffset.UtcNow.AddSeconds(3);
        var mailbox = await deliveries.PeekReadyMailboxAsync(now, now)
            ?? throw new InvalidOperationException("Missing mailbox.");
        await deliveries.TryLeaseBatchAsync(mailbox, Guid.NewGuid(), Guid.NewGuid(), now, now.AddSeconds(45), 64 * 1024);
        (await _reader.ListAsync(query)).ListVersion.Should().Be(completed.ListVersion);
        (await DetailAsync(running)).Task.DeliveryStatus.Should().Be(SubagentDeliveryStatus.Leased);
        await SubagentTaskTestData.CreateQueuedTaskAsync(_conversations, _tasks, parent);
        var added = () => _reader.ListAsync(query with { Cursor = completed.NextCursor });
        await added.Should().ThrowAsync<SubagentActivityReadException>().WithMessage("task-list-changed");
        var crossParent = () => _reader.ListAsync(query with { ParentConversationId = Guid.NewGuid(), Cursor = completed.NextCursor });
        await crossParent.Should().ThrowAsync<SubagentActivityReadException>().WithMessage("invalid-task-cursor");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetDetailAsync_marks_missing_history_and_keeps_unplaced_tools_separate(bool interrupted)
    {
        var task = await CreateRunningAsync();
        var tool = CreateTool(task, "recorded result");
        await _conversations.UpsertToolExecutionAsync(tool);
        var live = await DetailAsync(task);
        live.Content.Message.Should().BeNull();
        live.Content.HistoryCompleteness.Should().Be(SubagentHistoryCompleteness.Unknown);
        live.Content.UnplacedToolRuns.Should().ContainSingle();
        await CompleteAsync(task, interrupted ? SubagentTaskStatus.Interrupted : SubagentTaskStatus.Succeeded,
            "legacy final text", includeSegments: false, tool);
        var detail = await DetailAsync(task);
        detail.Content.HistoryCompleteness.Should().Be(SubagentHistoryCompleteness.Partial);
        detail.Content.Message.Should().NotBeNull();
        detail.Content.Message?.MarkdownContent.Should().Be("legacy final text");
        detail.Content.Message?.Segments.Should().BeEmpty();
        detail.Content.UnplacedToolRuns.Should().ContainSingle().Which.Id.Should().Be(tool.Id);
        detail.Content.TaskText.Should().Be(task.TaskText);
        detail.Task.DeliveryStatus.Should().Be(SubagentDeliveryStatus.Pending);
    }

    [Fact]
    public async Task GetDetailAsync_never_observes_half_of_a_concurrent_terminal_commit()
    {
        var task = await CreateRunningAsync();
        var tool = CreateTool(task, "tool result");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<SubagentActivityDetail>();
        var reads = Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < 30; index++)
            {
                observed.Add(await DetailAsync(task));
                await Task.Yield();
            }
        });
        var write = Task.Run(async () =>
        {
            await start.Task;
            await CompleteAsync(task, SubagentTaskStatus.Succeeded, tool: tool);
        });
        observed.Add(await DetailAsync(task));
        start.SetResult();
        await Task.WhenAll(reads, write);
        observed.Add(await DetailAsync(task));
        observed.Should().Contain(detail => detail.Task.Status == SubagentTaskStatus.Running)
            .And.Contain(detail => detail.Task.Status == SubagentTaskStatus.Succeeded);
        foreach (var detail in observed)
        {
            if (detail.Task.Status == SubagentTaskStatus.Succeeded)
            {
                detail.Task.DeliveryStatus.Should().Be(SubagentDeliveryStatus.Pending);
                var message = detail.Content.Message ?? throw new InvalidOperationException("A terminal task must have its committed assistant.");
                message.Status.Should().Be(MessageStatus.Completed);
                message.Segments.Should().HaveCount(3);
                detail.Content.ToolRuns.Should().ContainSingle();
            }
            else
            {
                detail.Content.Message.Should().BeNull();
                detail.Task.DeliveryStatus.Should().BeNull();
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try { Directory.Delete(_rootPath, recursive: true); }
            catch (IOException) { }
        }
    }

    private async Task<SubagentTaskRecord> CreateRunningAsync()
    {
        await _tasks.InitializeAsync();
        return await SubagentTaskTestData.CreateRunningTaskAsync(_conversations, _tasks);
    }

    private async Task<ConversationRecord> GetParentAsync(SubagentTaskRecord task)
        => await _conversations.GetConversationAsync(task.ParentConversationId) ?? throw new InvalidOperationException("Missing parent.");

    private async Task<SubagentActivityDetail> DetailAsync(SubagentTaskRecord task)
        => await _reader.GetDetailAsync(task.ParentConversationId, task.Id) ?? throw new InvalidOperationException("Missing detail.");

    private async Task CompleteAsync(SubagentTaskRecord task, SubagentTaskStatus status, string text = "answer",
        bool includeSegments = true, ToolExecutionRecord? tool = null)
    {
        var now = DateTimeOffset.UtcNow;
        var segments = new List<MessageSegmentRecord>
        {
            new(task.ChildTurnId, 0, MessageSegmentKind.Thinking, "analysis \n ", null),
            new(task.ChildTurnId, 1, MessageSegmentKind.Text, text, null)
        };
        if (tool is not null)
        {
            segments.Add(new MessageSegmentRecord(task.ChildTurnId, 2, MessageSegmentKind.ToolCall, null, tool.Id));
        }

        var messageStatus = status switch
        {
            SubagentTaskStatus.Succeeded => MessageStatus.Completed,
            SubagentTaskStatus.Cancelled => MessageStatus.Cancelled,
            _ => MessageStatus.Failed
        };
        var message = new MessageRecord(task.ChildTurnId, task.ChildConversationId, MessageRole.Assistant, text,
            messageStatus, now, now, Segments: includeSegments ? segments : null);
        var result = await _tasks.TryCompleteAsync(task.Id, task.Status, new SubagentTaskCompletion(status,
            new TurnFinalization(message, tool is null ? [] : [tool]), text, null, null, now));
        result.Should().NotBeNull();
    }

    private async Task<SubagentTaskRecord> CreateRetryAsync(SubagentTaskRecord previous, ConversationRecord parent)
    {
        var now = DateTimeOffset.UtcNow;
        var childId = Guid.NewGuid();
        var child = parent with { Id = childId, Kind = ConversationKind.Subagent, ParentConversationId = parent.Id };
        var retry = previous with
        {
            Id = Guid.NewGuid(), ChildConversationId = childId, ChildTurnId = Guid.NewGuid(), ParentTurnId = Guid.NewGuid(),
            Status = SubagentTaskStatus.Queued, Attempt = previous.Attempt + 1, RetryOfTaskId = previous.Id,
            QueuedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        await _tasks.CreateAsync(new SubagentTaskCreation(child,
            new MessageRecord(Guid.NewGuid(), childId, MessageRole.User, retry.TaskText, MessageStatus.Completed, now, now), retry));
        return retry;
    }

    private static ToolExecutionRecord CreateTool(SubagentTaskRecord task, string result)
        => new(Guid.NewGuid(), task.ChildConversationId, "read_file", "{\"path\":\"file.cs\"}", ToolExecutionStatus.Completed,
            "Read file", "call-1", 20, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MessageId: task.ChildTurnId,
            ResultContent: result, SourceKind: ToolSourceKind.BuiltIn);
}
