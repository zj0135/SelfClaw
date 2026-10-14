using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Automation;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Owns automation task validation and the destructive lifecycle operations. Saving and enabling keep the
/// TickerQ schedule converged with the task table; deletion cancels active runs, waits for them to settle
/// and only then cascades, so a timeout never leaves a half-deleted task.
/// </summary>
internal sealed class AutomationTaskService : IAutomationTaskService
{
    private static readonly TimeSpan DeletePollInterval = TimeSpan.FromMilliseconds(100);
    private const int ActiveRunScanLimit = 50;

    private readonly IAutomationTaskStore _taskStore;
    private readonly IAutomationRunStore _runStore;
    private readonly AutomationScheduleCalculator _calculator;
    private readonly AutomationSchedulerHost _scheduler;
    private readonly AutomationChangeNotifier _notifier;
    private readonly AutomationRunCancellationRegistry _cancellations;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _deleteWait;
    private readonly ILogger<AutomationTaskService> _logger;

    public AutomationTaskService(
        IAutomationTaskStore taskStore,
        IAutomationRunStore runStore,
        AutomationScheduleCalculator calculator,
        AutomationSchedulerHost scheduler,
        AutomationChangeNotifier notifier,
        AutomationRunCancellationRegistry cancellations,
        ILogger<AutomationTaskService> logger)
        : this(taskStore, runStore, calculator, scheduler, notifier, cancellations, TimeProvider.System,
            AutomationDefaults.DeleteWait, logger)
    {
    }

    internal AutomationTaskService(
        IAutomationTaskStore taskStore,
        IAutomationRunStore runStore,
        AutomationScheduleCalculator calculator,
        AutomationSchedulerHost scheduler,
        AutomationChangeNotifier notifier,
        AutomationRunCancellationRegistry cancellations,
        TimeProvider timeProvider,
        TimeSpan deleteWait,
        ILogger<AutomationTaskService> logger)
    {
        _taskStore = taskStore;
        _runStore = runStore;
        _calculator = calculator;
        _scheduler = scheduler;
        _notifier = notifier;
        _cancellations = cancellations;
        _timeProvider = timeProvider;
        _deleteWait = deleteWait;
        _logger = logger;
    }

    public async Task<AutomationTaskSaveResult> SaveAsync(
        AutomationTaskRecord task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var title = task.Title.Trim();
        if (title.Length == 0 || title.Length > AutomationDefaults.MaximumTitleLength)
        {
            return new AutomationTaskSaveResult(
                null,
                null,
                AutomationErrorCodes.InvalidTitle,
                $"任务标题必填，且不超过 {AutomationDefaults.MaximumTitleLength} 个字符。");
        }

        if (task.Kind != AutomationTaskKind.None && string.IsNullOrWhiteSpace(task.Prompt))
        {
            return new AutomationTaskSaveResult(
                null,
                null,
                AutomationErrorCodes.TriggerRequiresPrompt,
                "带触发器的任务必须填写提示词。");
        }

        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? nextRunAtUtc = null;
        if (task.IsEnabled && task.Kind != AutomationTaskKind.None &&
            !_calculator.TryComputeNextRun(task.Kind, task.Schedule, now, out nextRunAtUtc, out var errorCode))
        {
            return new AutomationTaskSaveResult(null, null, errorCode ?? AutomationErrorCodes.InvalidSchedule, ScheduleMessage(errorCode));
        }

        var saved = await _taskStore
            .SaveAsync(task with { Title = title, NextRunAtUtc = nextRunAtUtc, UpdatedAtUtc = now }, cancellationToken)
            .ConfigureAwait(false);
        _notifier.Notify("task-saved", saved.Id);
        await _scheduler.RequestRescheduleAsync(cancellationToken).ConfigureAwait(false);
        return new AutomationTaskSaveResult(saved, saved.NextRunAtUtc, null, null);
    }

    public async Task<AutomationTaskRecord?> SetEnabledAsync(
        Guid taskId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        var existing = await _taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? nextRunAtUtc = null;
        if (isEnabled && existing.Kind != AutomationTaskKind.None)
        {
            _calculator.TryComputeNextRun(existing.Kind, existing.Schedule, now, out nextRunAtUtc, out _);
        }

        var saved = await _taskStore.SaveAsync(
            existing with { IsEnabled = isEnabled, NextRunAtUtc = nextRunAtUtc, UpdatedAtUtc = now },
            cancellationToken).ConfigureAwait(false);
        _notifier.Notify("task-enabled", saved.Id);
        await _scheduler.RequestRescheduleAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<AutomationTaskDeleteResult> DeleteAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return new AutomationTaskDeleteResult(false, AutomationErrorCodes.UnknownTask, "该任务不存在。");
        }

        var now = _timeProvider.GetUtcNow();
        // Stop the schedule before cancelling so a sweep cannot queue a fresh run while we wait to drain.
        await _taskStore.SaveAsync(
            task with { IsEnabled = false, NextRunAtUtc = null, UpdatedAtUtc = now },
            cancellationToken).ConfigureAwait(false);
        await _scheduler.RequestRescheduleAsync(cancellationToken).ConfigureAwait(false);

        foreach (var active in await ListActiveRunsAsync(taskId, cancellationToken).ConfigureAwait(false))
        {
            _cancellations.CancelRun(active.Id);
        }

        if (!await WaitForActiveRunsAsync(taskId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Deleting automation task {TaskId} timed out waiting for its runs to stop.", taskId);
            return new AutomationTaskDeleteResult(
                false,
                AutomationErrorCodes.DeleteTimeout,
                $"在 {_deleteWait.TotalSeconds:0} 秒内未能停止该任务的运行，删除已中止。");
        }

        await _runStore.DeleteTerminalRunsAsync(taskId, null, cancellationToken).ConfigureAwait(false);
        await _taskStore.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);
        _notifier.Notify("task-deleted", taskId);
        await _scheduler.RequestRescheduleAsync(cancellationToken).ConfigureAwait(false);
        return new AutomationTaskDeleteResult(true, null, null);
    }

    public async Task<AutomationHistoryReclaimResult> ReclaimHistoryAsync(
        AutomationHistoryReclaimMode mode,
        Guid? taskId,
        Guid? runId,
        CancellationToken cancellationToken = default)
        => mode switch
        {
            AutomationHistoryReclaimMode.Run => await ReclaimRunAsync(runId, cancellationToken).ConfigureAwait(false),
            AutomationHistoryReclaimMode.TaskHistory => await ReclaimTaskHistoryAsync(taskId, cancellationToken).ConfigureAwait(false),
            AutomationHistoryReclaimMode.AllHistory => await ReclaimAllHistoryAsync(cancellationToken).ConfigureAwait(false),
            _ => new AutomationHistoryReclaimResult(0, "Unsupported", "Unsupported history reclaim mode.")
        };

    private async Task<AutomationHistoryReclaimResult> ReclaimRunAsync(Guid? runId, CancellationToken cancellationToken)
    {
        if (runId is not Guid targetRunId)
        {
            return new AutomationHistoryReclaimResult(0, AutomationErrorCodes.UnknownRun, "runId is required.");
        }

        var run = await _runStore.GetAsync(targetRunId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return new AutomationHistoryReclaimResult(0, AutomationErrorCodes.UnknownRun, "该运行记录不存在。");
        }

        if (run.Status is AutomationRunStatus.Queued or AutomationRunStatus.Running)
        {
            return new AutomationHistoryReclaimResult(0, AutomationErrorCodes.RunConflict, "正在进行的运行必须先停止。");
        }

        var deleted = await _runStore.DeleteTerminalRunsAsync(null, targetRunId, cancellationToken).ConfigureAwait(false);
        _notifier.Notify("run-deleted", run.TaskId, targetRunId);
        return new AutomationHistoryReclaimResult(deleted, null, null);
    }

    private async Task<AutomationHistoryReclaimResult> ReclaimTaskHistoryAsync(Guid? taskId, CancellationToken cancellationToken)
    {
        if (taskId is not Guid targetTaskId)
        {
            return new AutomationHistoryReclaimResult(0, AutomationErrorCodes.UnknownTask, "taskId is required.");
        }

        if (await _taskStore.GetAsync(targetTaskId, cancellationToken).ConfigureAwait(false) is null)
        {
            return new AutomationHistoryReclaimResult(0, AutomationErrorCodes.UnknownTask, "该任务不存在。");
        }

        var deleted = await _runStore.DeleteTerminalRunsAsync(targetTaskId, null, cancellationToken).ConfigureAwait(false);
        _notifier.Notify("history-cleared", targetTaskId);
        return new AutomationHistoryReclaimResult(deleted, null, null);
    }

    private async Task<AutomationHistoryReclaimResult> ReclaimAllHistoryAsync(CancellationToken cancellationToken)
    {
        var deleted = await _runStore.DeleteTerminalRunsAsync(null, null, cancellationToken).ConfigureAwait(false);
        _notifier.Notify("history-cleared");
        return new AutomationHistoryReclaimResult(deleted, null, null);
    }

    public Task<AutomationHistoryUsage> GetHistoryUsageAsync(CancellationToken cancellationToken = default)
        => _runStore.GetHistoryUsageAsync(cancellationToken);

    private async Task<IReadOnlyList<AutomationRunRecord>> ListActiveRunsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var runs = await _runStore.ListAsync(taskId, ActiveRunScanLimit, cancellationToken).ConfigureAwait(false);
        return runs.Where(run => run.Status is AutomationRunStatus.Queued or AutomationRunStatus.Running).ToArray();
    }

    private async Task<bool> WaitForActiveRunsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + _deleteWait;
        while (true)
        {
            if ((await ListActiveRunsAsync(taskId, cancellationToken).ConfigureAwait(false)).Count == 0)
            {
                return true;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(DeletePollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ScheduleMessage(string? errorCode)
        => errorCode == AutomationErrorCodes.IntervalTooShort
            ? $"触发间隔不能小于 {AutomationDefaults.MinimumInterval.TotalMinutes:0} 分钟。"
            : "触发器配置无效。";
}
