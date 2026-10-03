namespace SelfClaw.Core.Models;

public sealed record ConversationInputAcceptRequest(
    Guid ConversationId,
    string ClientRequestId,
    ConversationInputKind Kind,
    Guid? TargetTurnId,
    string Prompt,
    ConversationInputExecutionSnapshot? ExecutionSnapshot,
    ConversationRecord? Conversation,
    bool QueueEnabled,
    string? RequestFingerprint = null);
