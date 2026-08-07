namespace SelfClaw.Desktop.Services.Subagents;

internal static class SubagentErrorCodes
{
    internal const string DefinitionMissing = "DefinitionMissing";
    internal const string DefinitionInvalid = "DefinitionInvalid";
    internal const string ModelUnavailable = "ModelUnavailable";
    internal const string CapabilityNotAuthorized = "CapabilityNotAuthorized";
    internal const string CapabilityUnavailable = "CapabilityUnavailable";
    internal const string WorkspaceUnavailable = "WorkspaceUnavailable";
    internal const string TimedOut = "TimedOut";
    internal const string CancelledByParent = "CancelledByParent";
    internal const string ApplicationStopping = "ApplicationStopping";
    internal const string RuntimeCancelled = "RuntimeCancelled";
    internal const string ProcessInterrupted = "ProcessInterrupted";
    internal const string ProviderFailed = "ProviderFailed";
    internal const string SnapshotInvalid = "SnapshotInvalid";
}
