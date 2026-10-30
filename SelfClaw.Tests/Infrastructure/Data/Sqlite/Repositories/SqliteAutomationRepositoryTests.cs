using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteAutomationRepositoryTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Task_repository_round_trips_all_fields_and_updates_next_run()
    {
        var (taskStore, _, _) = await CreateStoresAsync();
        var task = CreateTask(nextRunAtUtc: DateTimeOffset.UtcNow.AddMinutes(30));

        await taskStore.SaveAsync(task);
        var loaded = await taskStore.GetAsync(task.Id);

        loaded.Should().NotBeNull();
        loaded.Should().BeEquivalentTo(task);

        var rescheduled = loaded! with { NextRunAtUtc = DateTimeOffset.UtcNow.AddHours(2), UpdatedAtUtc = DateTimeOffset.UtcNow };
        await taskStore.SaveAsync(rescheduled);

        (await taskStore.GetAsync(task.Id))!.NextRunAtUtc.Should().Be(rescheduled.NextRunAtUtc);
        (await taskStore.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Task_repository_sets_enabled_state_and_deletes()
    {
        var (taskStore, _, _) = await CreateStoresAsync();
        var task = CreateTask();
        await taskStore.SaveAsync(task);

        var disabled = await taskStore.SetEnabledAsync(task.Id, false, DateTimeOffset.UtcNow);
        disabled.Should().NotBeNull();
        disabled!.IsEnabled.Should().BeFalse();

        (await taskStore.SetEnabledAsync(Guid.NewGuid(), false, DateTimeOffset.UtcNow)).Should().BeNull();
        (await taskStore.DeleteAsync(task.Id)).Should().BeTrue();
        (await taskStore.DeleteAsync(task.Id)).Should().BeFalse();
        (await taskStore.GetAsync(task.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Run_insert_rejects_a_second_row_for_the_same_occurrence()
    {
        var (taskStore, runStore, database) = await CreateStoresAsync();
        var task = CreateTask();
        await taskStore.SaveAsync(task);
        var scheduledFor = DateTimeOffset.UtcNow;

        var first = await runStore.TryInsertAsync(CreateRun(task.Id, AutomationRunStatus.Queued, scheduledFor), skipWhenTaskActive: false);
        first.Should().NotBeNull();

        var duplicate = await runStore.TryInsertAsync(CreateRun(task.Id, AutomationRunStatus.Queued, scheduledFor), skipWhenTaskActive: false);
        duplicate.Should().BeNull();

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automation_runs(id, task_id, trigger, scheduled_for_utc, status, attempt, created_at_utc)
            VALUES($id, $taskId, 0, $scheduledFor, 0, 1, $at);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
        command.Parameters.AddWithValue("$scheduledFor", scheduledFor.ToString("O"));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        var action = () => command.ExecuteNonQueryAsync();

        await action.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Run_insert_treats_two_spellings_of_the_same_occurrence_as_one()
    {
        var (taskStore, runStore, _) = await CreateStoresAsync();
        var task = CreateTask();
        await taskStore.SaveAsync(task);
        // The sweep took the instant from automation_tasks.next_run_at_utc (local offset); the ticker took it from
        // TickerQ (UTC). Both describe the same occurrence.
        var fromTask = new DateTimeOffset(2026, 10, 30, 10, 30, 0, TimeSpan.FromHours(8));
        var fromTicker = new DateTimeOffset(fromTask.UtcDateTime);

        var first = await runStore.TryInsertAsync(
            CreateRun(task.Id, AutomationRunStatus.Running, fromTicker),
            skipWhenTaskActive: false);
        first.Should().NotBeNull();

        var sameOccurrence = await runStore.TryInsertAsync(
            CreateRun(task.Id, AutomationRunStatus.Queued, fromTask),
            skipWhenTaskActive: true);

        sameOccurrence.Should().BeNull();
        (await runStore.ListAsync(task.Id, 10)).Should().ContainSingle()
            .Which.Status.Should().Be(AutomationRunStatus.Running);
    }

    [Fact]
    public async Task Run_insert_recognizes_a_legacy_row_that_stored_a_local_offset()
    {
        var (taskStore, runStore, database) = await CreateStoresAsync();
        var task = CreateTask();
        await taskStore.SaveAsync(task);
        var localOffsetOccurrence = new DateTimeOffset(2026, 10, 30, 10, 30, 0, TimeSpan.FromHours(8));

        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO automation_runs(id, task_id, trigger, scheduled_for_utc, status, attempt, created_at_utc)
                VALUES($id, $taskId, 0, $scheduledFor, 1, 1, $at);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            command.Parameters.AddWithValue("$scheduledFor", localOffsetOccurrence.ToString("O"));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var sameOccurrence = await runStore.TryInsertAsync(
            CreateRun(task.Id, AutomationRunStatus.Queued, new DateTimeOffset(localOffsetOccurrence.UtcDateTime)),
            skipWhenTaskActive: true);

        sameOccurrence.Should().BeNull();
    }

    [Fact]
    public async Task Run_insert_records_overlap_when_the_task_is_active()
    {
        var (taskStore, runStore, _) = await CreateStoresAsync();
        var task = CreateTask();
        await taskStore.SaveAsync(task);
        await runStore.TryInsertAsync(CreateRun(task.Id, AutomationRunStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-5)), skipWhenTaskActive: false);

        var inserted = await runStore.TryInsertAsync(
            CreateRun(task.Id, AutomationRunStatus.Queued, DateTimeOffset.UtcNow),
            skipWhenTaskActive: true);

        inserted.Should().NotBeNull();
        inserted!.Status.Should().Be(AutomationRunStatus.Skipped);
        inserted.SkipReason.Should().Be(AutomationRunSkipReason.Overlap);
        var runs = await runStore.ListAsync(task.Id, 10);
        runs.Should().HaveCount(2);
        runs.Should().NotContain(run => run.Status == AutomationRunStatus.Queued);
    }

    [Fact]
    public async Task Run_claim_and_complete_are_conditional()
    {
        var (taskStore, runStore, _) = await CreateStoresAsync();
        var task = CreateTask();
        await taskStore.SaveAsync(task);
        var run = CreateRun(task.Id, AutomationRunStatus.Queued, DateTimeOffset.UtcNow);
        await runStore.TryInsertAsync(run, skipWhenTaskActive: false);

        var claimed = await runStore.TryClaimAsync(run.Id, DateTimeOffset.UtcNow);
        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be(AutomationRunStatus.Running);
        (await runStore.TryClaimAsync(run.Id, DateTimeOffset.UtcNow)).Should().BeNull();

        var completed = await runStore.TryCompleteAsync(run.Id, claimed with
        {
            Status = AutomationRunStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });

        completed.Should().NotBeNull();
        completed!.Status.Should().Be(AutomationRunStatus.Completed);
        (await runStore.TryCompleteAsync(run.Id, claimed)).Should().BeNull();
    }

    [Fact]
    public async Task Saving_a_task_with_a_prompt_forces_full_access_while_a_todo_keeps_its_mode()
    {
        var (taskStore, _, _) = await CreateStoresAsync();
        var prompted = CreateTask() with { ToolPermissionMode = ToolPermissionMode.RequireApproval };
        var todo = CreateTask() with { Kind = AutomationTaskKind.None, Prompt = null, ToolPermissionMode = ToolPermissionMode.RequireApproval };

        await taskStore.SaveAsync(prompted);
        await taskStore.SaveAsync(todo);

        (await taskStore.GetAsync(prompted.Id))!.ToolPermissionMode.Should().Be(ToolPermissionMode.FullAccess);
        (await taskStore.GetAsync(todo.Id))!.ToolPermissionMode.Should().Be(ToolPermissionMode.RequireApproval);
    }

    [Fact]
    public async Task Record_attempt_persists_the_resolved_model_snapshot_and_workspace()
    {
        var (taskStore, runStore, database) = await CreateStoresAsync();
        var task = CreateTask();
        await taskStore.SaveAsync(task);
        var run = CreateRun(task.Id, AutomationRunStatus.Queued, DateTimeOffset.UtcNow);
        await runStore.TryInsertAsync(run, skipWhenTaskActive: false);
        var claimed = await runStore.TryClaimAsync(run.Id, DateTimeOffset.UtcNow);
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Automation",
            null,
            ConversationMode.Programming,
            ToolPermissionMode.FullAccess,
            "automation:fixture",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Kind: ConversationKind.Automation);
        await new SqliteConversationRepository(database).UpsertConversationAsync(conversation);
        var attemptStore = (IAutomationRunAttemptStore)runStore;
        var modelProfileId = Guid.NewGuid();

        var recorded = await attemptStore.RecordAttemptAsync(new AutomationRunAttempt(
            run.Id, 2, conversation.Id, modelProfileId, "{\"version\":1}", null, null, DateTimeOffset.UtcNow));

        recorded.Should().NotBeNull();
        recorded!.ConversationId.Should().Be(conversation.Id);
        recorded.Attempt.Should().Be(2);
        recorded.ModelProfileId.Should().Be(modelProfileId);
        recorded.CapabilitySnapshotJson.Should().Be("{\"version\":1}");
        (await attemptStore.RecordAttemptAsync(new AutomationRunAttempt(
            claimed!.Id, 3, conversation.Id, modelProfileId, "{}", null, null, DateTimeOffset.UtcNow))).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_terminal_runs_removes_the_conversation_and_transcript_but_keeps_active_runs()
    {
        var (taskStore, runStore, database) = await CreateStoresAsync();
        var task = CreateTask();
        await taskStore.SaveAsync(task);
        var seeded = await SeedTerminalRunWithConversationAsync(database, task.Id, "summarize the nightly diff");
        var active = CreateRun(task.Id, AutomationRunStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-1));
        await runStore.TryInsertAsync(active, skipWhenTaskActive: false);
        var conversations = new SqliteConversationRepository(database);

        var deleted = await runStore.DeleteTerminalRunsAsync(null, seeded.Run.Id);

        deleted.Should().Be(1);
        (await runStore.GetAsync(seeded.Run.Id)).Should().BeNull();
        (await runStore.GetAsync(active.Id)).Should().NotBeNull();
        (await conversations.GetConversationAsync(seeded.ConversationId)).Should().BeNull();
        (await conversations.ListMessagesAsync(seeded.ConversationId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_terminal_runs_scoped_to_a_task_leaves_other_tasks_untouched()
    {
        var (taskStore, runStore, database) = await CreateStoresAsync();
        var taskA = CreateTask();
        var taskB = CreateTask();
        await taskStore.SaveAsync(taskA);
        await taskStore.SaveAsync(taskB);
        var runA = await SeedTerminalRunWithConversationAsync(database, taskA.Id, "a");
        var runB = await SeedTerminalRunWithConversationAsync(database, taskB.Id, "b");

        var deleted = await runStore.DeleteTerminalRunsAsync(taskA.Id, null);

        deleted.Should().Be(1);
        (await runStore.GetAsync(runA.Run.Id)).Should().BeNull();
        (await runStore.GetAsync(runB.Run.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task History_usage_sums_message_text_per_task_and_globally()
    {
        var (taskStore, runStore, database) = await CreateStoresAsync();
        var taskA = CreateTask();
        var taskB = CreateTask();
        await taskStore.SaveAsync(taskA);
        await taskStore.SaveAsync(taskB);
        await SeedTerminalRunWithConversationAsync(database, taskA.Id, "abcdefghij");
        await SeedTerminalRunWithConversationAsync(database, taskB.Id, "12345");

        var usage = await runStore.GetHistoryUsageAsync();

        usage.TotalBytes.Should().Be(15);
        usage.Tasks.Should().Contain(item => item.TaskId == taskA.Id && item.Bytes == 10);
        usage.Tasks.Should().Contain(item => item.TaskId == taskB.Id && item.Bytes == 5);
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

    private async Task<(IAutomationTaskStore TaskStore, IAutomationRunStore RunStore, SqliteDatabase Database)> CreateStoresAsync()
    {
        var database = new SqliteDatabase(StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets")));
        await database.EnsureInitializedAsync();
        return (new SqliteAutomationTaskRepository(database), new SqliteAutomationRunRepository(database), database);
    }

    private static AutomationTaskRecord CreateTask(DateTimeOffset? nextRunAtUtc = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationTaskRecord(
            Guid.NewGuid(),
            "Daily standup",
            "notes",
            AutomationTaskKind.Daily,
            new AutomationScheduleSpec(Time: "09:30", Weekdays: [1, 3, 5], DayOfMonth: 15),
            nextRunAtUtc,
            "Summarize the day",
            "instructions",
            "system",
            ["plugin-a"],
            ["skill-a", "skill-b"],
            ["mcp-a"],
            Guid.NewGuid(),
            null,
            ToolPermissionMode.FullAccess,
            AutomationTaskStatus.Open,
            null,
            now.AddDays(1),
            true,
            now.AddDays(-1),
            AutomationRunStatus.Completed,
            now,
            now);
    }

    private static async Task<(AutomationRunRecord Run, Guid ConversationId, int PromptLength)> SeedTerminalRunWithConversationAsync(
        SqliteDatabase database,
        Guid taskId,
        string prompt)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Automation",
            null,
            ConversationMode.Programming,
            ToolPermissionMode.FullAccess,
            $"automation:{taskId:D}",
            now,
            now,
            Kind: ConversationKind.Automation);
        var conversations = new SqliteConversationRepository(database);
        await conversations.UpsertConversationAsync(conversation);
        var turns = new SqliteConversationTurnRepository(database, new SqliteTurnCommitGate());
        var turn = new ConversationTurnRecord(
            Guid.NewGuid(),
            conversation.Id,
            AgentExecutionMode.Direct,
            DirectTurnOrigin.Automation,
            ConversationTurnStatus.Running,
            now);
        await turns.StartTurnAsync(new ConversationTurnStart(conversation, turn, prompt, Guid.NewGuid()));
        var run = CreateRun(taskId, AutomationRunStatus.Completed, now.AddMinutes(-1)) with
        {
            ConversationId = conversation.Id,
            CompletedAtUtc = now
        };
        await new SqliteAutomationRunRepository(database).TryInsertAsync(run, skipWhenTaskActive: false);
        return (run, conversation.Id, prompt.Length);
    }

    private static AutomationRunRecord CreateRun(Guid taskId, AutomationRunStatus status, DateTimeOffset scheduledForUtc)
        => new(
            Guid.NewGuid(),
            taskId,
            null,
            AutomationRunTrigger.Scheduled,
            scheduledForUtc,
            status,
            null,
            1,
            null,
            null,
            null,
            null,
            status == AutomationRunStatus.Running ? scheduledForUtc : null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
}
