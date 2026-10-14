namespace SelfClaw.Core.Runtime;

public static class AutomationErrorCodes
{
    public const string TriggerRequiresPrompt = "TriggerRequiresPrompt";
    public const string InvalidTitle = "InvalidTitle";
    public const string InvalidSchedule = "InvalidSchedule";
    public const string IntervalTooShort = "IntervalTooShort";
    public const string RunConflict = "RunConflict";
    public const string UnknownTask = "UnknownTask";
    public const string UnknownRun = "UnknownRun";
    public const string DeleteTimeout = "DeleteTimeout";
    public const string ModelUnavailable = "ModelUnavailable";
    public const string CapabilityUnavailable = "CapabilityUnavailable";
    public const string WorkspaceUnavailable = "WorkspaceUnavailable";
    public const string ProviderFailed = "ProviderFailed";
    public const string TimedOut = "TimedOut";
    public const string ToolFaultLimit = "ToolFaultLimit";
    public const string ProcessInterrupted = "ProcessInterrupted";
    public const string CancelledByUser = "CancelledByUser";
    public const string ApplicationStopping = "ApplicationStopping";
}
