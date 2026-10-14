using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Automation;
using SelfClaw.Infrastructure.Automation;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Automation;

public sealed class AutomationTaskServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_validates_the_title_the_prompt_and_the_schedule()
    {
        var harness = await CreateHarnessAsync();

        var noTitle = await harness.Service.SaveAsync(CreateTask() with { Title = "  " });
        noTitle.ErrorCode.Should().Be(AutomationErrorCodes.InvalidTitle);
        noTitle.Task.Should().BeNull();

        var noPrompt = await harness.Service.SaveAsync(CreateTask() with { Prompt = null });
        noPrompt.ErrorCode.Should().Be(AutomationErrorCodes.TriggerRequiresPrompt);

        var tooShort = await harness.Service.SaveAsync(CreateTask() with
        {
            Kind = AutomationTaskKind.Cron,
            Schedule = new AutomationScheduleSpec(Cron: "* * * * *")
        });
        tooShort.ErrorCode.Should().Be(AutomationErrorCodes.IntervalTooShort);

        var saved = await harness.Service.SaveAsync(CreateTask());
        saved.Task.Should().NotBeNull();
        saved.NextRunAtUtc.Should().NotBeNull();
        saved.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_run_rejects_an_active_run_and_reclaims_a_terminal_one()
    {
        var harness = await CreateHarnessAsync();
        var task = CreateTask();
        await harness.TaskStore.SaveAsync(task);
        var terminal = await SeedTerminalRunAsync(harness.Database, task.Id, "abcdefghij");
        var active = CreateRun(task.Id, AutomationRunStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.RunStore.TryInsertAsync(active, skipWhenTaskActive: false);

        var conflict = await harness.Service.ReclaimHistoryAsync(AutomationHistoryReclaimMode.Run, null, active.Id);
        conflict.ErrorCode.Should().Be(AutomationErrorCodes.RunConflict);
        (await harness.RunStore.GetAsync(active.Id)).Should().NotBeNull();

        var reclaimed = await harness.Service.ReclaimHistoryAsync(AutomationHistoryReclaimMode.Run, null, terminal.RunId);
        reclaimed.DeletedRuns.Should().Be(1);
        reclaimed.ErrorCode.Should().BeNull();
        (await harness.RunStore.GetAsync(terminal.RunId)).Should().BeNull();
        (await new SqliteConversationRepository(harness.Database).GetConversationAsync(terminal.ConversationId)).Should().BeNull();
        (await harness.RunStore.GetAsync(active.Id)).Should().NotBeNull();
        harness.Reasons.Should().Contain("run-deleted");
    }

    [Fact]
    public async Task Clearing_task_history_keeps_the_definition_and_active_runs()
    {
        var harness = await CreateHarnessAsync();
        var task = CreateTask();
        var other = CreateTask() with { Title = "Other" };
        await harness.TaskStore.SaveAsync(task);
        await harness.TaskStore.SaveAsync(other);
        await SeedTerminalRunAsync(harness.Database, task.Id, "one");
        await SeedTerminalRunAsync(harness.Database, task.Id, "two");
        var otherRun = await SeedTerminalRunAsync(harness.Database, other.Id, "three");
        var active = CreateRun(task.Id, AutomationRunStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.RunStore.TryInsertAsync(active, skipWhenTaskActive: false);

        var result = await harness.Service.ReclaimHistoryAsync(AutomationHistoryReclaimMode.TaskHistory, task.Id, null);

        result.DeletedRuns.Should().Be(2);
        (await harness.TaskStore.GetAsync(task.Id)).Should().NotBeNull();
        (await harness.RunStore.GetAsync(active.Id)).Should().NotBeNull();
        (await harness.RunStore.GetAsync(otherRun.RunId)).Should().NotBeNull();
        harness.Reasons.Should().Contain("history-cleared");
    }

    [Fact]
    public async Task Clearing_all_history_reclaims_every_terminal_run_across_tasks()
    {
        var harness = await CreateHarnessAsync();
        var task = CreateTask();
        var other = CreateTask() with { Title = "Other" };
        await harness.TaskStore.SaveAsync(task);
        await harness.TaskStore.SaveAsync(other);
        await SeedTerminalRunAsync(harness.Database, task.Id, "one");
        await SeedTerminalRunAsync(harness.Database, other.Id, "two");

        var result = await harness.Service.ReclaimHistoryAsync(AutomationHistoryReclaimMode.AllHistory, null, null);

        result.DeletedRuns.Should().Be(2);
        (await harness.TaskStore.ListAsync()).Should().HaveCount(2);
        (await harness.RunStore.ListAsync(null, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_task_with_only_terminal_runs_cascades_definition_runs_and_conversations()
    {
        var harness = await CreateHarnessAsync();
        var task = CreateTask();
        await harness.TaskStore.SaveAsync(task);
        var terminal = await SeedTerminalRunAsync(harness.Database, task.Id, "one");

        var result = await harness.Service.DeleteAsync(task.Id);

        result.Deleted.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        (await harness.TaskStore.GetAsync(task.Id)).Should().BeNull();
        (await harness.RunStore.GetAsync(terminal.RunId)).Should().BeNull();
        (await new SqliteConversationRepository(harness.Database).GetConversationAsync(terminal.ConversationId)).Should().BeNull();
        harness.Reasons.Should().Contain("task-deleted");
    }

    [Fact]
    public async Task Deleting_a_task_times_out_without_partial_deletion_when_a_run_never_stops()
    {
        var harness = await CreateHarnessAsync(TimeSpan.FromMilliseconds(250));
        var task = CreateTask();
        await harness.TaskStore.SaveAsync(task);
        var active = CreateRun(task.Id, AutomationRunStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.RunStore.TryInsertAsync(active, skipWhenTaskActive: false);

        var result = await harness.Service.DeleteAsync(task.Id);

        result.Deleted.Should().BeFalse();
        result.ErrorCode.Should().Be(AutomationErrorCodes.DeleteTimeout);
        (await harness.TaskStore.GetAsync(task.Id)).Should().NotBeNull();
        (await harness.RunStore.GetAsync(active.Id)).Should().NotBeNull();
        harness.Reasons.Should().NotContain("task-deleted");
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

    private async Task<Harness> CreateHarnessAsync(TimeSpan? deleteWait = null)
    {
        var database = new SqliteDatabase(StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets")));
        await database.EnsureInitializedAsync();
        var taskStore = new SqliteAutomationTaskRepository(database);
        var runStore = new SqliteAutomationRunRepository(database);
        var calculator = new AutomationScheduleCalculator(TimeZoneInfo.Local);
        var notifier = new AutomationChangeNotifier();
        var reasons = new List<string>();
        notifier.Changed += (reason, _, _) => reasons.Add(reason);
        var cancellations = new AutomationRunCancellationRegistry();
        var host = new AutomationSchedulerHost(
            taskStore,
            runStore,
            calculator,
            new NoopRecovery(),
            new NoopExecutor(),
            new NoopRegistrar(),
            cancellations,
            notifier,
            new NoopRunNotifier(),
            TimeProvider.System,
            NullLogger<AutomationSchedulerHost>.Instance);
        await host.StartSchedulingAsync(CancellationToken.None);
        var service = new AutomationTaskService(
            taskStore,
            runStore,
            calculator,
            host,
            notifier,
            cancellations,
            TimeProvider.System,
            deleteWait ?? AutomationDefaults.DeleteWait,
            NullLogger<AutomationTaskService>.Instance);
        return new Harness(service, taskStore, runStore, database, reasons);
    }

    private static async Task<(Guid RunId, Guid ConversationId)> SeedTerminalRunAsync(
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
        return (run.Id, conversation.Id);
    }

    private static AutomationTaskRecord CreateTask()
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationTaskRecord(
            Guid.NewGuid(),
            "Daily standup",
            string.Empty,
            AutomationTaskKind.Daily,
            new AutomationScheduleSpec(Time: "09:30"),
            null,
            "Summarize the day",
            string.Empty,
            "system",
            [],
            [],
            [],
            null,
            null,
            ToolPermissionMode.FullAccess,
            AutomationTaskStatus.Open,
            null,
            null,
            true,
            null,
            null,
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
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

    private sealed record Harness(
        IAutomationTaskService Service,
        IAutomationTaskStore TaskStore,
        IAutomationRunStore RunStore,
        SqliteDatabase Database,
        List<string> Reasons);

    private sealed class NoopRecovery : IAutomationStartupRecovery
    {
        public Task<IReadOnlyList<AutomationRunRecord>> RecoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AutomationRunRecord>>([]);
    }

    private sealed class NoopRegistrar : IAutomationScheduleRegistrar
    {
        public Task UpsertCronAsync(Guid taskId, string cronExpression, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertOnceAsync(Guid taskId, DateTimeOffset scheduledForUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(Guid taskId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoopExecutor : IAutomationRunExecutor
    {
        public Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class NoopRunNotifier : IAutomationRunNotifier
    {
        public Task NotifyAsync(AutomationTaskRecord task, AutomationRunRecord run, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
