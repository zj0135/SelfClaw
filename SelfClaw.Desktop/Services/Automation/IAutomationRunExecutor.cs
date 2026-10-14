using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Execution seam. P1 ships <see cref="AutomationRunPlaceholderExecutor"/>; the real headless Direct turn
/// replaces it without touching the scheduler.
/// </summary>
internal interface IAutomationRunExecutor
{
    Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken);
}
