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

public sealed class AutomationSchedulerHostTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Ticker_records_overlap_and_does_not_queue_when_the_task_is_running()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), now.AddHours(2));
        await harness.TaskStore.SaveAsync(task);
        await harness.Host.StartSchedulingAsync(CancellationToken.None);
        await harness.RunStore.TryInsertAsync(
            CreateRun(task.Id, AutomationRunTrigger.Scheduled, AutomationRunStatus.Running, now.AddMinutes(-5)),
            skipWhenTaskActive: false);

        await harness.Host.HandleTickerAsync(new AutomationTickerPayload(task.Id, null), now, CancellationToken.None);

        harness.FakeExecutor.ExecutedRuns.Should().BeEmpty();
        harness.RunNotifier.Runs.Should().BeEmpty();
        var runs = await harness.RunStore.ListAsync(task.Id, 10);
        runs.Should().Contain(run => run.Status == AutomationRunStatus.Skipped && run.SkipReason == AutomationRunSkipReason.Overlap);
        runs.Should().NotContain(run => run.Status == AutomationRunStatus.Queued);
    }

    [Fact]
    public async Task Missed_occurrences_within_grace_merge_into_one_catch_up_run()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Cron, new AutomationScheduleSpec(Cron: "0 * * * *"), now.AddHours(-3));
        await harness.TaskStore.SaveAsync(task);

        await harness.Host.StartSchedulingAsync(CancellationToken.None);
        await harness.Host.SweepMissedRunsAsync(CancellationToken.None);

        harness.FakeExecutor.ExecutedRuns.Should().ContainSingle();
        var runs = await harness.RunStore.ListAsync(task.Id, 10);
        runs.Should().ContainSingle().Which.Trigger.Should().Be(AutomationRunTrigger.CatchUp);
        (await harness.TaskStore.GetAsync(task.Id))!.NextRunAtUtc.Should().BeAfter(now);
    }

    [Fact]
    public async Task Missed_occurrences_beyond_grace_are_recorded_as_stale()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), now.AddHours(-7));
        await harness.TaskStore.SaveAsync(task);

        await harness.Host.StartSchedulingAsync(CancellationToken.None);
        await harness.Host.SweepMissedRunsAsync(CancellationToken.None);

        harness.FakeExecutor.ExecutedRuns.Should().BeEmpty();
        var runs = await harness.RunStore.ListAsync(task.Id, 10);
        runs.Should().ContainSingle().Which.SkipReason.Should().Be(AutomationRunSkipReason.Stale);
        (await harness.TaskStore.GetAsync(task.Id))!.NextRunAtUtc.Should().BeAfter(now);
    }

    [Fact]
    public async Task Late_ticker_beyond_grace_is_recorded_as_stale()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), now.AddHours(-7));
        await harness.TaskStore.SaveAsync(task);

        await harness.Host.StartSchedulingAsync(CancellationToken.None);
        await harness.Host.HandleTickerAsync(new AutomationTickerPayload(task.Id, null), now.AddHours(-7), CancellationToken.None);

        harness.FakeExecutor.ExecutedRuns.Should().BeEmpty();
        var runs = await harness.RunStore.ListAsync(task.Id, 10);
        runs.Should().ContainSingle().Which.SkipReason.Should().Be(AutomationRunSkipReason.Stale);
    }

    [Fact]
    public async Task Rebuild_is_idempotent_for_cron_and_once_tickers()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var daily = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), now.AddHours(1));
        var cron = CreateTask(AutomationTaskKind.Cron, new AutomationScheduleSpec(Cron: "*/15 * * * *"), now.AddHours(1));
        var once = CreateTask(AutomationTaskKind.Once, new AutomationScheduleSpec(RunAtUtc: now.AddHours(3)), now.AddHours(3));
        await harness.TaskStore.SaveAsync(daily);
        await harness.TaskStore.SaveAsync(cron);
        await harness.TaskStore.SaveAsync(once);

        await harness.Host.RebuildScheduleAsync(CancellationToken.None);
        await harness.Host.RebuildScheduleAsync(CancellationToken.None);

        harness.Registrar.CronTickers.Should().HaveCount(2);
        harness.Registrar.CronTickers[daily.Id].Should().Be("0 9 * * *");
        harness.Registrar.CronTickers[cron.Id].Should().Be("*/15 * * * *");
        harness.Registrar.OnceTickers.Should().ContainSingle();
        (await harness.RunStore.ListAsync(null, 100)).Should().BeEmpty();
    }

    [Fact]
    public async Task Completed_run_advances_the_task_schedule()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), now.AddMinutes(-1));
        await harness.TaskStore.SaveAsync(task);

        await harness.Host.StartSchedulingAsync(CancellationToken.None);
        await harness.Host.SweepMissedRunsAsync(CancellationToken.None);

        harness.FakeExecutor.ExecutedRuns.Should().ContainSingle();
        var reloaded = await harness.TaskStore.GetAsync(task.Id);
        reloaded!.LastRunStatus.Should().Be(AutomationRunStatus.Completed);
        reloaded.NextRunAtUtc.Should().BeAfter(now);
    }

    [Fact]
    public async Task Once_task_success_marks_the_task_done_and_stops_rescheduling()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Once, new AutomationScheduleSpec(RunAtUtc: now.AddMinutes(-1)), now.AddMinutes(-1));
        await harness.TaskStore.SaveAsync(task);
        await harness.Host.StartSchedulingAsync(CancellationToken.None);

        await harness.Host.SweepMissedRunsAsync(CancellationToken.None);

        var reloaded = await harness.TaskStore.GetAsync(task.Id);
        reloaded!.Status.Should().Be(AutomationTaskStatus.Done);
        reloaded.CompletedAtUtc.Should().NotBeNull();
        reloaded.NextRunAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Once_task_failure_keeps_it_open_without_rescheduling()
    {
        var harness = await CreateHarnessAsync(runStore => new FailingExecutor(runStore));
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Once, new AutomationScheduleSpec(RunAtUtc: now.AddMinutes(-1)), now.AddMinutes(-1));
        await harness.TaskStore.SaveAsync(task);
        await harness.Host.StartSchedulingAsync(CancellationToken.None);

        await harness.Host.SweepMissedRunsAsync(CancellationToken.None);

        var reloaded = await harness.TaskStore.GetAsync(task.Id);
        reloaded!.Status.Should().Be(AutomationTaskStatus.Open);
        reloaded.CompletedAtUtc.Should().BeNull();
        reloaded.NextRunAtUtc.Should().BeNull();
        (await harness.RunStore.ListAsync(task.Id, 10)).Should().ContainSingle()
            .Which.Status.Should().Be(AutomationRunStatus.Failed);
        harness.RunNotifier.Runs.Should().ContainSingle().Which.Status.Should().Be(AutomationRunStatus.Failed);
    }

    [Fact]
    public async Task Startup_recovery_notifies_about_the_runs_it_interrupts()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), now.AddHours(-1));
        await harness.TaskStore.SaveAsync(task);
        var running = CreateRun(task.Id, AutomationRunTrigger.Scheduled, AutomationRunStatus.Running, now.AddMinutes(-1));
        await harness.RunStore.TryInsertAsync(running, skipWhenTaskActive: false);

        await harness.Host.StartSchedulingAsync(CancellationToken.None);

        harness.RunNotifier.Runs.Should().ContainSingle(item => item.RunId == running.Id && item.Status == AutomationRunStatus.Interrupted);
    }

    [Fact]
    public async Task Restarting_within_grace_after_a_hard_kill_starts_a_fresh_catch_up_run_without_resuming_the_interrupted_one()
    {
        var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), now.AddMinutes(-10));
        await harness.TaskStore.SaveAsync(task);
        var running = CreateRun(task.Id, AutomationRunTrigger.Scheduled, AutomationRunStatus.Running, now.AddMinutes(-5));
        await harness.RunStore.TryInsertAsync(running, skipWhenTaskActive: false);

        await harness.Host.StartSchedulingAsync(CancellationToken.None);
        await harness.Host.SweepMissedRunsAsync(CancellationToken.None);

        var runs = await harness.RunStore.ListAsync(task.Id, 10);
        runs.Should().Contain(run => run.Id == running.Id && run.Status == AutomationRunStatus.Interrupted);
        runs.Should().ContainSingle(run => run.Trigger == AutomationRunTrigger.CatchUp && run.Status == AutomationRunStatus.Completed);
        harness.FakeExecutor.ExecutedRuns.Should().ContainSingle().Which.Should().NotBe(running.Id);
    }

    [Fact]
    public async Task Cancellation_registry_cancels_a_running_run_and_the_host_records_application_stopping()
    {
        var executor = new BlockingExecutor();
        var harness = await CreateHarnessAsync(_ => executor);
        var now = DateTimeOffset.UtcNow;
        var task = CreateTask(AutomationTaskKind.Daily, new AutomationScheduleSpec(Time: "09:00"), now.AddMinutes(-1));
        await harness.TaskStore.SaveAsync(task);
        await harness.Host.StartSchedulingAsync(CancellationToken.None);

        var sweep = Task.Run(() => harness.Host.SweepMissedRunsAsync(CancellationToken.None));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Cancellations.CancelAll();
        try
        {
            await sweep.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }

        var run = (await harness.RunStore.ListAsync(task.Id, 1)).Single();
        run.Status.Should().Be(AutomationRunStatus.Cancelled);
        run.ErrorCode.Should().Be(AutomationErrorCodes.ApplicationStopping);
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

    private async Task<Harness> CreateHarnessAsync(Func<IAutomationRunStore, IAutomationRunExecutor>? executorFactory = null)
    {
        var database = new SqliteDatabase(StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets")));
        await database.EnsureInitializedAsync();
        var taskStore = new SqliteAutomationTaskRepository(database);
        var runStore = new SqliteAutomationRunRepository(database);
        var registrar = new FakeAutomationScheduleRegistrar();
        var cancellations = new AutomationRunCancellationRegistry();
        var runNotifier = new RecordingRunNotifier();
        var fakeExecutor = new FakeAutomationRunExecutor(runStore);
        var runExecutor = executorFactory?.Invoke(runStore) ?? fakeExecutor;
        var host = new AutomationSchedulerHost(
            taskStore,
            runStore,
            new AutomationScheduleCalculator(TimeZoneInfo.Local),
            new AutomationStartupRecovery(database),
            runExecutor,
            registrar,
            cancellations,
            new AutomationChangeNotifier(),
            runNotifier,
            TimeProvider.System,
            NullLogger<AutomationSchedulerHost>.Instance);
        return new Harness(host, taskStore, runStore, registrar, fakeExecutor, cancellations, runNotifier);
    }

    private static AutomationTaskRecord CreateTask(
        AutomationTaskKind kind,
        AutomationScheduleSpec schedule,
        DateTimeOffset? nextRunAtUtc)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationTaskRecord(
            Guid.NewGuid(),
            "Automation task",
            string.Empty,
            kind,
            schedule,
            nextRunAtUtc,
            "Do the thing",
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

    private static AutomationRunRecord CreateRun(
        Guid taskId,
        AutomationRunTrigger trigger,
        AutomationRunStatus status,
        DateTimeOffset scheduledForUtc)
        => new(
            Guid.NewGuid(),
            taskId,
            null,
            trigger,
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

    private sealed record Harness(
        AutomationSchedulerHost Host,
        IAutomationTaskStore TaskStore,
        IAutomationRunStore RunStore,
        FakeAutomationScheduleRegistrar Registrar,
        FakeAutomationRunExecutor FakeExecutor,
        AutomationRunCancellationRegistry Cancellations,
        RecordingRunNotifier RunNotifier);

    private sealed class RecordingRunNotifier : IAutomationRunNotifier
    {
        public List<(Guid RunId, AutomationRunStatus Status)> Runs { get; } = [];

        public Task NotifyAsync(AutomationTaskRecord task, AutomationRunRecord run, CancellationToken cancellationToken = default)
        {
            Runs.Add((run.Id, run.Status));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAutomationScheduleRegistrar : IAutomationScheduleRegistrar
    {
        public Dictionary<Guid, string> CronTickers { get; } = [];

        public Dictionary<Guid, DateTimeOffset> OnceTickers { get; } = [];

        public Task UpsertCronAsync(Guid taskId, string cronExpression, CancellationToken cancellationToken)
        {
            CronTickers[taskId] = cronExpression;
            OnceTickers.Remove(taskId);
            return Task.CompletedTask;
        }

        public Task UpsertOnceAsync(Guid taskId, DateTimeOffset scheduledForUtc, CancellationToken cancellationToken)
        {
            OnceTickers[taskId] = scheduledForUtc;
            CronTickers.Remove(taskId);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid taskId, CancellationToken cancellationToken)
        {
            CronTickers.Remove(taskId);
            OnceTickers.Remove(taskId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAutomationRunExecutor : IAutomationRunExecutor
    {
        private readonly IAutomationRunStore _runStore;

        public FakeAutomationRunExecutor(IAutomationRunStore runStore)
        {
            _runStore = runStore;
        }

        public List<Guid> ExecutedRuns { get; } = [];

        public async Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken)
        {
            ExecutedRuns.Add(run.Id);
            await _runStore.TryCompleteAsync(run.Id, run with
            {
                Status = AutomationRunStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
    }

    private sealed class BlockingExecutor : IAutomationRunExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingExecutor : IAutomationRunExecutor
    {
        private readonly IAutomationRunStore _runStore;

        public FailingExecutor(IAutomationRunStore runStore)
        {
            _runStore = runStore;
        }

        public Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken)
            => _runStore.TryCompleteAsync(run.Id, run with
            {
                Status = AutomationRunStatus.Failed,
                ErrorCode = AutomationErrorCodes.ProviderFailed,
                CompletedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
    }
}
