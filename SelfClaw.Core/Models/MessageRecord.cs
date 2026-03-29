namespace SelfClaw.Core.Models;

public sealed record MessageRecord(
    Guid Id,
    Guid ConversationId,
    MessageRole Role,
    string MarkdownContent,
    MessageStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int? InputTokens = null,
    int? OutputTokens = null,
    double? DurationMs = null,
    string? ErrorMessage = null);