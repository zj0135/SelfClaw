namespace SelfClaw.Core.Models;

public sealed record ConversationInputAcceptRequest(
    Guid ConversationId,
    string ClientRequestId,
    string Prompt,
    ConversationInputExecutionSnapshot? ExecutionSnapshot,
    ConversationRecord? Conversation,
    string? RequestFingerprint = null);
