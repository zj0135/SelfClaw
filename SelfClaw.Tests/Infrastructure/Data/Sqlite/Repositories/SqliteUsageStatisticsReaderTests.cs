using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteUsageStatisticsReaderTests : IDisposable
{
    private readonly string _rootPath;

    public SqliteUsageStatisticsReaderTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task GetConversationUsageAsync_groups_by_model_and_reports_cache_hit_rate()
    {
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        await conversations.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        await conversations.UpsertConversationAsync(new ConversationRecord(
            conversationId,
            "Usage",
            WorkspaceRootId: null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now));
        await conversations.UpsertMessageAsync(new MessageRecord(
            Guid.NewGuid(), conversationId, MessageRole.User, "Hello", MessageStatus.Completed, now, now));
        await conversations.UpsertMessageAsync(AssistantMessage(
            conversationId,
            now,
            new TurnUsage(
                Model: "model-a",
                InputTokens: 100,
                UncachedInputTokens: 60,
                CachedInputTokens: 40,
                OutputTokens: 10,
                ProviderCalls: 2,
                CostUsdMicros: 1_500,
                CostSource: TurnUsageCostSource.ProviderReported)));
        await conversations.UpsertMessageAsync(AssistantMessage(
            conversationId,
            now,
            new TurnUsage(
                Model: "model-b",
                InputTokens: 50,
                UncachedInputTokens: 50,
                OutputTokens: 5,
                ProviderCalls: 1)));

        var report = await new SqliteUsageStatisticsReader(database).GetConversationUsageAsync(conversationId);

        report.Models.Should().HaveCount(2);
        var modelA = report.Models.Single(model => model.Model == "model-a");
        modelA.Totals.InputTokens.Should().Be(100);
        modelA.Totals.CachedInputTokens.Should().Be(40);
        modelA.Totals.CostUsdMicros.Should().Be(1_500);
        modelA.Totals.TurnCount.Should().Be(1);
        modelA.Totals.PricedTurnCount.Should().Be(1);
        modelA.CacheHitRate.Should().BeApproximately(0.4, 0.0001);
        var modelB = report.Models.Single(model => model.Model == "model-b");
        modelB.Totals.PricedTurnCount.Should().Be(0);
        modelB.CacheHitRate.Should().Be(0);
        report.Totals.InputTokens.Should().Be(150);
        report.Totals.ProviderCalls.Should().Be(3);
        report.Totals.TurnCount.Should().Be(2);
        report.Totals.PricedTurnCount.Should().Be(1);
        report.Totals.CostUsdMicros.Should().Be(1_500);
        report.CacheHitRate.Should().BeApproximately(40d / 150d, 0.0001);
    }

    [Fact]
    public async Task GetSubagentUsageAsync_keeps_child_usage_out_of_the_parent_report()
    {
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversations = new SqliteConversationRepository(database);
        await conversations.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var parentConversationId = Guid.NewGuid();
        var childConversationId = Guid.NewGuid();
        await conversations.UpsertConversationAsync(new ConversationRecord(
            parentConversationId,
            "Parent",
            WorkspaceRootId: null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now));
        await conversations.UpsertConversationAsync(new ConversationRecord(
            childConversationId,
            "Subagent",
            WorkspaceRootId: null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "worker",
            now,
            now,
            Kind: ConversationKind.Subagent,
            ParentConversationId: parentConversationId));
        await conversations.UpsertMessageAsync(AssistantMessage(
            parentConversationId,
            now,
            new TurnUsage(Model: "parent-model", InputTokens: 100, UncachedInputTokens: 100, OutputTokens: 10)));
        var childMessage = AssistantMessage(
            childConversationId,
            now,
            new TurnUsage(Model: "child-model", InputTokens: 20, UncachedInputTokens: 20, OutputTokens: 2));
        await conversations.UpsertMessageAsync(childMessage);
        await InsertSubagentTaskAsync(database, parentConversationId, childConversationId, childMessage.Id, now);

        var reader = new SqliteUsageStatisticsReader(database);
        var parentReport = await reader.GetConversationUsageAsync(parentConversationId);
        var subagentReport = await reader.GetSubagentUsageAsync(parentConversationId);

        parentReport.Models.Should().ContainSingle().Which.Model.Should().Be("parent-model");
        parentReport.Totals.InputTokens.Should().Be(100);
        subagentReport.Models.Should().ContainSingle().Which.Model.Should().Be("child-model");
        subagentReport.Totals.InputTokens.Should().Be(20);
        subagentReport.Totals.TurnCount.Should().Be(1);
    }

    private static MessageRecord AssistantMessage(Guid conversationId, DateTimeOffset now, TurnUsage usage)
        => new(
            Guid.NewGuid(),
            conversationId,
            MessageRole.Assistant,
            "answer",
            MessageStatus.Completed,
            now,
            now,
            Usage: usage);

    private static async Task InsertSubagentTaskAsync(
        SqliteDatabase database,
        Guid parentConversationId,
        Guid childConversationId,
        Guid childTurnId,
        DateTimeOffset now)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO subagent_tasks(
    id, parent_conversation_id, parent_turn_id, child_conversation_id, child_turn_id,
    subagent_id, subagent_name, task_text, status, attempt,
    definition_snapshot_json, parent_execution_snapshot_json, max_run_seconds,
    queued_at_utc, created_at_utc, updated_at_utc)
VALUES(
    $id, $parentConversationId, $parentTurnId, $childConversationId, $childTurnId,
    $subagentId, $subagentName, $taskText, $status, 1,
    $definitionSnapshot, $parentSnapshot, 60,
    $now, $now, $now);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$parentConversationId", parentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$parentTurnId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$childConversationId", childConversationId.ToString("D"));
        command.Parameters.AddWithValue("$childTurnId", childTurnId.ToString("D"));
        command.Parameters.AddWithValue("$subagentId", "worker");
        command.Parameters.AddWithValue("$subagentName", "Worker");
        command.Parameters.AddWithValue("$taskText", "Do the work.");
        command.Parameters.AddWithValue("$status", (int)SubagentTaskStatus.Succeeded);
        command.Parameters.AddWithValue("$definitionSnapshot", "{}");
        command.Parameters.AddWithValue("$parentSnapshot", "{}");
        command.Parameters.AddWithValue("$now", now.ToString("O"));
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
            Directory.Delete(_rootPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
