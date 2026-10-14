using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Automation;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Owns Automation timing. TickerQ is a rebuildable wake-up source: the truth lives in
/// automation_tasks, and every rebuild converges one ticker per task. Recovery completes before the first
/// ticker may start a run.
/// </summary>
internal sealed class AutomationSchedulerHost : BackgroundService
{
    private readonly IAutomationTaskStore _taskStore;
    private readonly IAutomationRunStore _runStore;
    private readonly AutomationScheduleCalculator _calculator;
    private readonly IAutomationStartupRecovery _recovery;
    private readonly IAutomationRunExecutor _executor;
    private readonly IAutomationScheduleRegistrar _registrar;
    private readonly AutomationRunCancellationRegistry _cancellations;
    private readonly AutomationChangeNotifier _notifier;
    private readonly IAutomationRunNotifier _runNotifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AutomationSchedulerHost> _logger;
    private readonly TaskCompletionSource _recoveryCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, Task> _runningExecutions = new();
    private bool _scheduleDirty;

    public AutomationSchedulerHost(
        IAutomationTaskStore taskStore,
        IAutomationRunStore runStore,
        AutomationScheduleCalculator calculator,
        IAutomationStartupRecovery recovery,
        IAutomationRunExecutor executor,
        IAutomationScheduleRegistrar registrar,
        AutomationRunCancellationRegistry cancellations,
        AutomationChangeNotifier notifier,
        IAutomationRunNotifier runNotifier,
        ILogger<AutomationSchedulerHost> logger)
        : this(taskStore, runStore, calculator, recovery, executor, registrar, cancellations, notifier, runNotifier, TimeProvider.System, logger)
    {
    }

    internal AutomationSchedulerHost(
        IAutomationTaskStore taskStore,
        IAutomationRunStore runStore,
        AutomationScheduleCalculator calculator,
        IAutomationStartupRecovery recovery,
        IAutomationRunExecutor executor,
        IAutomationScheduleRegistrar registrar,
        AutomationRunCancellationRegistry cancellations,
        AutomationChangeNotifier notifier,
        IAutomationRunNotifier runNotifier,
        TimeProvider timeProvider,
        ILogger<AutomationSchedulerHost> logger)
    {
        _taskStore = taskStore;
        _runStore = runStore;
        _calculator = calculator;
        _recovery = recovery;
        _executor = executor;
        _registrar = registrar;
        _cancellations = cancellations;
        _notifier = notifier;
        _runNotifier = runNotifier;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartSchedulingAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _recoveryCompleted.TrySetResult();
            return;
        }
        catch (Exception exception)
        {
            _recoveryCompleted.TrySetException(exception);
            throw;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepMissedRunsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Automation sweep failed.");
                }

                await Task.Delay(AutomationDefaults.ScanInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellations.CancelAll();
            try
            {
                await Task.WhenAll(_runningExecutions.Values)
                    .WaitAsync(AutomationDefaults.ShutdownCancelWait, _timeProvider, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Automation shutdown did not drain within {Seconds} seconds.", AutomationDefaults.ShutdownCancelWait.TotalSeconds);
            }
        }
    }

    internal async Task StartSchedulingAsync(CancellationToken cancellationToken)
    {
        var interrupted = await _recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
        await RebuildScheduleAsync(cancellationToken).ConfigureAwait(false);
        _recoveryCompleted.TrySetResult();
        foreach (var run in interrupted)
        {
            var task = await _taskStore.GetAsync(run.TaskId, cancellationToken).ConfigureAwait(false);
            if (task is not null)
            {
                await NotifyRunAsync(task, run with { Status = AutomationRunStatus.Interrupted }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal async Task HandleTickerAsync(
        AutomationTickerPayload payload,
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await _recoveryCompleted.Task.ConfigureAwait(false);
        var task = await _taskStore.GetAsync(payload.TaskId, cancellationToken).ConfigureAwait(false);
        if (task is null || !task.IsEnabled || task.Kind == AutomationTaskKind.None)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (AutomationScheduleCalculator.ClassifyMissed(scheduledForUtc, now) == AutomationMissedRunDisposition.Stale)
        {
            await InsertSkippedAsync(task, AutomationRunTrigger.Scheduled, scheduledForUtc, AutomationRunSkipReason.Stale, now, cancellationToken).ConfigureAwait(false);
            await AdvanceTaskAsync(task, null, now, cancellationToken).ConfigureAwait(false);
            _notifier.Notify("run-skipped", task.Id);
            return;
        }

        var queued = CreateRun(task, AutomationRunTrigger.Scheduled, scheduledForUtc, now);
        var inserted = await _runStore.TryInsertAsync(queued, skipWhenTaskActive: true, cancellationToken).ConfigureAwait(false);
        if (inserted is null)
        {
            return;
        }

        if (inserted.Status == AutomationRunStatus.Skipped)
        {
            _notifier.Notify("run-skipped", task.Id, inserted.Id);
            return;
        }

        _notifier.Notify("run-queued", task.Id, inserted.Id);
        await ClaimAndExecuteAsync(task, inserted, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SweepMissedRunsAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var tasks = await _taskStore.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var task in tasks)
        {
            if (!task.IsEnabled || task.Kind == AutomationTaskKind.None)
            {
                continue;
            }

            if (task.NextRunAtUtc is not DateTimeOffset due || due > now)
            {
                continue;
            }

            if (AutomationScheduleCalculator.ClassifyMissed(due, now) == AutomationMissedRunDisposition.Stale)
            {
                await InsertSkippedAsync(task, AutomationRunTrigger.CatchUp, due, AutomationRunSkipReason.Stale, now, cancellationToken).ConfigureAwait(false);
                await AdvanceTaskAsync(task, null, now, cancellationToken).ConfigureAwait(false);
                _notifier.Notify("run-skipped", task.Id);
                continue;
            }

            var queued = CreateRun(task, AutomationRunTrigger.CatchUp, due, now);
            var inserted = await _runStore.TryInsertAsync(queued, skipWhenTaskActive: true, cancellationToken).ConfigureAwait(false);
            if (inserted is null)
            {
                continue;
            }

            if (inserted.Status == AutomationRunStatus.Skipped)
            {
                _notifier.Notify("run-skipped", task.Id, inserted.Id);
                continue;
            }

            _notifier.Notify("run-queued", task.Id, inserted.Id);
            await ClaimAndExecuteAsync(task, inserted, cancellationToken).ConfigureAwait(false);
        }

        if (_scheduleDirty)
        {
            _scheduleDirty = false;
            await RebuildScheduleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task RebuildScheduleAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var tasks = await _taskStore.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var task in tasks)
        {
            if (!task.IsEnabled || task.Kind == AutomationTaskKind.None)
            {
                await _registrar.RemoveAsync(task.Id, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (task.Kind == AutomationTaskKind.Once)
            {
                await RebuildOnceAsync(task, now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await RebuildRecurringAsync(task, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RebuildOnceAsync(AutomationTaskRecord task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var next = task.NextRunAtUtc ?? ComputeNextRun(task, now);
        if (task.NextRunAtUtc is null && next is not null)
        {
            await _taskStore.SaveAsync(task with { NextRunAtUtc = next }, cancellationToken).ConfigureAwait(false);
        }

        if (next is DateTimeOffset scheduledFor && scheduledFor > now)
        {
            await _registrar.UpsertOnceAsync(task.Id, scheduledFor, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _registrar.RemoveAsync(task.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task RebuildRecurringAsync(AutomationTaskRecord task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (task.NextRunAtUtc is null)
        {
            var next = ComputeNextRun(task, now);
            if (next is not null)
            {
                await _taskStore.SaveAsync(task with { NextRunAtUtc = next }, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!_calculator.TryBuildCronExpression(task.Kind, task.Schedule, out var expression, out _))
        {
            await _registrar.RemoveAsync(task.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _registrar.UpsertCronAsync(task.Id, expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Converges the TickerQ registration with the task table. Called after any task mutation so the
    /// schedule reflects the new definition immediately instead of on the next sweep.
    /// </summary>
    internal async Task RequestRescheduleAsync(CancellationToken cancellationToken)
    {
        await _recoveryCompleted.Task.ConfigureAwait(false);
        await RebuildScheduleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues and starts a manual run. When the task already has a Queued/Running run the returned record
    /// is the Skipped(Overlap) row, which lets the bridge answer RunConflict without queuing behind it.
    /// </summary>
    internal async Task<AutomationRunRecord?> TryStartManualRunAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null || string.IsNullOrWhiteSpace(task.Prompt))
        {
            return null;
        }

        await _recoveryCompleted.Task.ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var queued = CreateRun(task, AutomationRunTrigger.Manual, null, now);
        var inserted = await _runStore.TryInsertAsync(queued, skipWhenTaskActive: true, cancellationToken).ConfigureAwait(false);
        if (inserted is null)
        {
            return null;
        }

        if (inserted.Status == AutomationRunStatus.Skipped)
        {
            _notifier.Notify("run-skipped", task.Id, inserted.Id);
            return inserted;
        }

        _notifier.Notify("run-queued", task.Id, inserted.Id);
        var claimed = await _runStore.TryClaimAsync(inserted.Id, now, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            return inserted;
        }

        _notifier.Notify("run-started", task.Id, claimed.Id);
        StartExecution(task, claimed, cancellationToken);
        return claimed;
    }

    private async Task ClaimAndExecuteAsync(
        AutomationTaskRecord task,
        AutomationRunRecord queuedRun,
        CancellationToken cancellationToken)
    {
        var claimed = await _runStore.TryClaimAsync(queuedRun.Id, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            return;
        }

        _notifier.Notify("run-started", task.Id, claimed.Id);
        var execution = ExecuteClaimedAsync(task, claimed, cancellationToken);
        _runningExecutions[claimed.Id] = execution;
        try
        {
            await execution.ConfigureAwait(false);
        }
        finally
        {
            _runningExecutions.TryRemove(claimed.Id, out _);
        }
    }

    private void StartExecution(AutomationTaskRecord task, AutomationRunRecord run, CancellationToken cancellationToken)
    {
        var execution = ExecuteClaimedAsync(task, run, cancellationToken);
        _runningExecutions[run.Id] = execution;
        _ = execution.ContinueWith(
            _ => _runningExecutions.TryRemove(run.Id, out Task? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ExecuteClaimedAsync(
        AutomationTaskRecord task,
        AutomationRunRecord run,
        CancellationToken cancellationToken)
    {
        var token = _cancellations.BeginRun(run.Id);
        try
        {
            await _executor.ExecuteAsync(run, task, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var current = await _runStore.GetAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            if (current is not null && !IsTerminal(current.Status))
            {
                await _runStore.TryCompleteAsync(run.Id, current with
                {
                    Status = AutomationRunStatus.Cancelled,
                    ErrorCode = AutomationErrorCodes.ApplicationStopping,
                    ErrorMessage = null,
                    CompletedAtUtc = _timeProvider.GetUtcNow()
                }, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _cancellations.CompleteRun(run.Id);
            var terminal = await _runStore.GetAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            await AdvanceTaskAsync(task, terminal, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            if (terminal is not null)
            {
                await NotifyRunAsync(task, terminal, CancellationToken.None).ConfigureAwait(false);
            }

            _notifier.Notify("run-finished", task.Id, run.Id);
        }
    }

    private async Task NotifyRunAsync(
        AutomationTaskRecord task,
        AutomationRunRecord run,
        CancellationToken cancellationToken)
    {
        try
        {
            await _runNotifier.NotifyAsync(task, run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Automation run notification for {RunId} failed.", run.Id);
        }
    }

    private async Task AdvanceTaskAsync(
        AutomationTaskRecord task,
        AutomationRunRecord? terminal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var updated = task;
        if (terminal is not null)
        {
            updated = updated with
            {
                LastRunAtUtc = terminal.CompletedAtUtc ?? terminal.StartedAtUtc ?? now,
                LastRunStatus = terminal.Status
            };
            if (task.Kind == AutomationTaskKind.Once && IsTerminal(terminal.Status))
            {
                updated = terminal.Status == AutomationRunStatus.Completed
                    ? updated with { Status = AutomationTaskStatus.Done, CompletedAtUtc = terminal.CompletedAtUtc ?? now }
                    : updated with { Status = AutomationTaskStatus.Open, CompletedAtUtc = null };
            }
        }

        updated = updated with { NextRunAtUtc = ComputeNextRun(updated, now), UpdatedAtUtc = now };
        await _taskStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        _scheduleDirty = true;
    }

    private async Task InsertSkippedAsync(
        AutomationTaskRecord task,
        AutomationRunTrigger trigger,
        DateTimeOffset scheduledForUtc,
        AutomationRunSkipReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var skipped = new AutomationRunRecord(
            Guid.NewGuid(),
            task.Id,
            null,
            trigger,
            scheduledForUtc,
            AutomationRunStatus.Skipped,
            reason,
            1,
            task.ModelProfileId,
            null,
            task.WorkspaceRootId,
            null,
            null,
            now,
            null,
            null,
            now);
        await _runStore.TryInsertAsync(skipped, skipWhenTaskActive: false, cancellationToken).ConfigureAwait(false);
    }

    private AutomationRunRecord CreateRun(
        AutomationTaskRecord task,
        AutomationRunTrigger trigger,
        DateTimeOffset? scheduledForUtc,
        DateTimeOffset now)
        => new(
            Guid.NewGuid(),
            task.Id,
            null,
            trigger,
            scheduledForUtc,
            AutomationRunStatus.Queued,
            null,
            1,
            task.ModelProfileId,
            null,
            task.WorkspaceRootId,
            null,
            null,
            null,
            null,
            null,
            now);

    private DateTimeOffset? ComputeNextRun(AutomationTaskRecord task, DateTimeOffset now)
    {
        if (!task.IsEnabled || task.Kind == AutomationTaskKind.None)
        {
            return null;
        }

        return _calculator.TryComputeNextRun(task.Kind, task.Schedule, now, out var nextRunAtUtc, out _)
            ? nextRunAtUtc
            : null;
    }

    private static bool IsTerminal(AutomationRunStatus status)
        => status is not (AutomationRunStatus.Queued or AutomationRunStatus.Running);
}
