using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// P1 placeholder. It records the run as failed so the scheduler seam is exercised end to end; the real
/// headless Direct turn replaces this type in P2.
/// </summary>
internal sealed class AutomationRunPlaceholderExecutor : IAutomationRunExecutor
{
    private readonly IAutomationRunStore _runStore;
    private readonly TimeProvider _timeProvider;

    public AutomationRunPlaceholderExecutor(IAutomationRunStore runStore)
        : this(runStore, TimeProvider.System)
    {
    }

    internal AutomationRunPlaceholderExecutor(IAutomationRunStore runStore, TimeProvider timeProvider)
    {
        _runStore = runStore;
        _timeProvider = timeProvider;
    }

    public async Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(task);
        var terminal = run with
        {
            Status = AutomationRunStatus.Failed,
            ErrorCode = AutomationErrorCodes.NotImplemented,
            ErrorMessage = "Automation execution is not implemented in this phase.",
            CompletedAtUtc = _timeProvider.GetUtcNow()
        };
        await _runStore.TryCompleteAsync(run.Id, terminal, cancellationToken).ConfigureAwait(false);
    }
}
