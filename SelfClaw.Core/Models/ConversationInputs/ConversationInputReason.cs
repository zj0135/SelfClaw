namespace SelfClaw.Core.Models;

/// <summary>
/// Reason codes for queue transitions. They are persisted so the UI never has to infer state
/// from localized copy.
/// </summary>
public static class ConversationInputReason
{
    public const string QueueDisabled = "queue-disabled";
    public const string Paused = "paused";
    public const string Capacity = "capacity";
    public const string AttachmentsUnsupported = "attachments-unsupported";
    public const string InterruptedByRestart = "interrupted-by-restart";
    public const string StaleSteerTarget = "stale-steer-target";
    public const string AgentChanged = "agent-changed";
    public const string AgentMissing = "agent-missing";
    public const string ModelDisabled = "model-disabled";
    public const string WorkspaceMissing = "workspace-missing";
    public const string ExecutionStartFailed = "execution-start-failed";
    public const string TurnFailed = "turn-failed";
    public const string TurnCancelled = "turn-cancelled";
    public const string TurnTruncated = "turn-truncated";
    public const string TurnBlocked = "turn-blocked";
    public const string TurnInterrupted = "turn-interrupted";
    public const string PersistFailed = "persist-failed";
    public const string QueuePaused = "queue-paused";
}
