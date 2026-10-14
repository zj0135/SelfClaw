using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Settings;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Raises the automation toasts. Failures (including interrupted runs) always notify; successes are opt-in
/// through the "automation" settings node so a daily task does not toast every day. Skipped runs never notify.
/// </summary>
internal sealed class AutomationRunNotifier : IAutomationRunNotifier
{
    private const string SettingsNodeName = "automation";
    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DesktopNotificationService _notifications;
    private readonly DesktopSettingsJsonStore _settings;

    public AutomationRunNotifier(DesktopNotificationService notifications, DesktopSettingsJsonStore settings)
    {
        _notifications = notifications;
        _settings = settings;
    }

    public async Task NotifyAsync(
        AutomationTaskRecord task,
        AutomationRunRecord run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(run);
        var notifyOnSuccess = run.Status == AutomationRunStatus.Completed && await ReadNotifyOnSuccessAsync(cancellationToken).ConfigureAwait(false);
        if (!ShouldNotify(run.Status, notifyOnSuccess))
        {
            return;
        }

        if (run.Status == AutomationRunStatus.Completed)
        {
            _notifications.ShowAutomationRunSucceeded(task.Id, run.Id, task.Title, "自动化任务已完成。");
            return;
        }

        _notifications.ShowAutomationRunFailed(task.Id, run.Id, task.Title, ResolveFailureMessage(run));
    }

    internal static bool ShouldNotify(AutomationRunStatus status, bool notifyOnSuccess)
        => status switch
        {
            AutomationRunStatus.Failed or AutomationRunStatus.Interrupted => true,
            AutomationRunStatus.Completed => notifyOnSuccess,
            _ => false
        };

    private async Task<bool> ReadNotifyOnSuccessAsync(CancellationToken cancellationToken)
        => (await _settings.ReadNodeAsync<AutomationSettings>(SettingsNodeName, SettingsJsonOptions, cancellationToken)
            .ConfigureAwait(false))?.NotifyOnSuccess ?? false;

    private static string ResolveFailureMessage(AutomationRunRecord run)
    {
        if (run.ErrorMessage is { Length: > 0 } message)
        {
            return message;
        }

        return run.Status == AutomationRunStatus.Interrupted ? "自动化运行被中断。" : "自动化运行失败。";
    }
}
