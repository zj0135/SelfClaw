namespace SelfClaw.Core.Runtime;

public static class SubagentErrorCodes
{
    public const string DefinitionMissing = "DefinitionMissing";
    public const string DefinitionInvalid = "DefinitionInvalid";
    public const string ModelUnavailable = "ModelUnavailable";
    public const string CapabilityNotAuthorized = "CapabilityNotAuthorized";
    public const string CapabilityUnavailable = "CapabilityUnavailable";
    public const string WorkspaceUnavailable = "WorkspaceUnavailable";
    public const string TimedOut = "TimedOut";
    public const string CancelledByParent = "CancelledByParent";
    public const string ApplicationStopping = "ApplicationStopping";
    public const string RuntimeCancelled = "RuntimeCancelled";
    public const string ProcessInterrupted = "ProcessInterrupted";
    public const string ProviderFailed = "ProviderFailed";
    public const string OutputTruncated = "OutputTruncated";
    public const string SnapshotInvalid = "SnapshotInvalid";
}
