namespace SelfClaw.Core.Models;

public sealed record SubagentCompletionEnvelope(
    int Version,
    Guid DeliveryId,
    Guid TaskId,
    Guid ParentTurnId,
    Guid ChildConversationId,
    SubagentIdentity Subagent,
    string Task,
    SubagentTaskStatus Status,
    int Attempt,
    SubagentCompletionResult Result,
    TurnUsage? Usage,
    SubagentTiming Timing);
