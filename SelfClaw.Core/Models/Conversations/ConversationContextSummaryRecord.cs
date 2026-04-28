namespace SelfClaw.Core.Models;

public sealed record ConversationContextSummaryRecord(
    Guid ConversationId,
    string SummaryMarkdown,
    Guid? CoveredThroughMessageId,
    DateTimeOffset? CoveredThroughMessageCreatedAtUtc,
    int SourceTokenEstimate,
    int SummaryTokenEstimate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
