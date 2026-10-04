namespace SelfClaw.Core.Models;

/// <summary>
/// Reason codes for queue transitions and input rejections. Transition reasons are persisted so
/// the UI never has to infer state from localized copy.
/// </summary>
public static class ConversationInputReason
{
    public const string Capacity = "capacity";
    public const string AttachmentsUnsupported = "attachments-unsupported";
    public const string DirectOnly = "direct-only";
    public const string ModelUnavailable = "model-unavailable";
    public const string RequestConflict = "request-conflict";
    public const string ConversationUnavailable = "conversation-unavailable";
    public const string QueueRevisionConflict = "queue-revision-conflict";
    public const string SubscriptionInvalid = "conversation-input-subscription-invalid";
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
    public const string TargetTurnClosed = "target-turn-closed";
    public const string ContinuationActive = "continuation-active";
    public const string HookRequiresNewTurn = "hook-requires-new-turn";
    public const string SkillRequiresNewTurn = "skill-requires-new-turn";
    public const string SupersededBySteer = "superseded-by-steer";
    public const string ContextBudgetExceeded = "context-budget-exceeded";
}
