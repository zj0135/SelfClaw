using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Automation;

internal interface IAutomationRunNotifier
{
    Task NotifyAsync(AutomationTaskRecord task, AutomationRunRecord run, CancellationToken cancellationToken = default);
}
