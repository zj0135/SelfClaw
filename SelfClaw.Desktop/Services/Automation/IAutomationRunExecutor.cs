using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Execution seam: the scheduler owns when a run starts, the executor owns what running it means.
/// </summary>
internal interface IAutomationRunExecutor
{
    Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken);
}
