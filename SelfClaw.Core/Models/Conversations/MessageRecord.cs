namespace SelfClaw.Core.Models;

public sealed record MessageRecord(
    Guid Id,
    Guid ConversationId,
    MessageRole Role,
    string MarkdownContent,
    MessageStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? AgentId = null,
    string? AgentName = null,
    string? AgentRole = null,
    TurnUsage? Usage = null,
    double? DurationMs = null,
    string? ErrorMessage = null,
    IReadOnlyList<MessageAttachmentRecord>? Attachments = null,
    IReadOnlyList<MessageSegmentRecord>? Segments = null);
