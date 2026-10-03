namespace SelfClaw.Core.Models;

public sealed record ConversationTurnStart(
    ConversationRecord Conversation,
    ConversationTurnRecord Turn,
    string Prompt,
    Guid UserMessageId,
    IReadOnlyList<MessageAttachmentRecord>? Attachments = null);
