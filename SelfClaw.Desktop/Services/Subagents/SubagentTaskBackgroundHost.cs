using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentTaskBackgroundHost : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);
    private readonly ISubagentTaskExecutionStore _taskStore;
    private readonly SubagentTaskExecutor _executor;
    private readonly SubagentTaskWakeSignal _wakeSignal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubagentTaskBackgroundHost> _logger;
    private readonly HashSet<Task> _runningExecutions = [];

    public SubagentTaskBackgroundHost(
        ISubagentTaskExecutionStore taskStore,
        SubagentTaskExecutor executor,
        SubagentTaskWakeSignal wakeSignal,
        ILogger<SubagentTaskBackgroundHost> logger)
        : this(taskStore, executor, wakeSignal, TimeProvider.System, logger)
    {
    }

    internal SubagentTaskBackgroundHost(
        ISubagentTaskExecutionStore taskStore,
        SubagentTaskExecutor executor,
        SubagentTaskWakeSignal wakeSignal,
        TimeProvider timeProvider,
        ILogger<SubagentTaskBackgroundHost> logger)
    {
        _taskStore = taskStore;
        _executor = executor;
        _wakeSignal = wakeSignal;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverInterruptedTasksAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                RemoveCompletedExecutions();
                var claimedAny = await ClaimAvailableTasksAsync(stoppingToken);
                if (!claimedAny)
                {
                    await _wakeSignal.WaitAsync(ScanInterval, _timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(_runningExecutions.ToArray());
        }
    }

    private async Task RecoverInterruptedTasksAsync(CancellationToken cancellationToken)
    {
        var interrupted = await _taskStore.ListByStatusAsync(
            SubagentTaskStatus.Running,
            cancellationToken);
        foreach (var task in interrupted)
        {
            await _executor.RecoverInterruptedAsync(task, cancellationToken);
        }
    }

    private async Task<bool> ClaimAvailableTasksAsync(CancellationToken cancellationToken)
    {
        var claimedAny = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var task = await _taskStore.TryClaimNextAsync(
                _timeProvider.GetUtcNow(),
                cancellationToken);
            if (task is null)
            {
                return claimedAny;
            }

            claimedAny = true;
            _runningExecutions.Add(RunTaskAsync(task, cancellationToken));
        }

        return claimedAny;
    }

    private async Task RunTaskAsync(SubagentTaskRecord task, CancellationToken cancellationToken)
    {
        try
        {
            await _executor.ExecuteAsync(task, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Subagent task {TaskId} worker failed.", task.Id);
        }
        finally
        {
            _wakeSignal.Signal();
        }
    }

    private void RemoveCompletedExecutions()
        => _runningExecutions.RemoveWhere(task => task.IsCompleted);
}
