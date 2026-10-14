namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Fans automation mutations out to the shell. The scheduler raises it from background threads, so
/// subscribers must marshal to their own thread before touching the WebView channel.
/// </summary>
internal sealed class AutomationChangeNotifier
{
    public event Action<string, Guid?, Guid?>? Changed;

    internal void Notify(string reason, Guid? taskId = null, Guid? runId = null)
        => Changed?.Invoke(reason, taskId, runId);
}
