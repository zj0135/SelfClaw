using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
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
