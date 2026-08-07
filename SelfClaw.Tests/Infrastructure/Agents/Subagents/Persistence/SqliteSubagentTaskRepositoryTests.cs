using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
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
        var tasks = new SqliteSubagentTaskRepository(database);
        await tasks.InitializeAsync();
        return new TestContext(database, conversations, tasks);
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

    private static async Task<long> CountAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private sealed record TestContext(
        SqliteDatabase Database,
        SqliteConversationRepository Conversations,
        SqliteSubagentTaskRepository Tasks);
}
