namespace SelfClaw.Core.Models;

public sealed record ConversationInputAcceptRequest(
    Guid ConversationId,
    string ClientRequestId,
    ConversationInputKind Kind,
    Guid? TargetTurnId,
    string Prompt,
    ConversationInputExecutionSnapshot? ExecutionSnapshot,
    ConversationRecord? Conversation,
    string? RequestFingerprint = null,
    string? ReasonCode = null);
