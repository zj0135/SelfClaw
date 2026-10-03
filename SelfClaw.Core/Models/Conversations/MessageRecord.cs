namespace SelfClaw.Core.Models;

public sealed record MessageRecord(
    Guid Id,
    Guid ConversationId,
    Guid TurnId,
    long Sequence,
    MessageRole Role,
    string MarkdownContent,
    MessageStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? AgentId = null,
    string? AgentName = null,
    string? AgentRole = null,
    IReadOnlyList<MessageAttachmentRecord>? Attachments = null,
    IReadOnlyList<MessageSegmentRecord>? Segments = null);
