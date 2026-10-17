namespace SelfClaw.Core.Models;

/// <summary>
/// A persisted summary that replaces the conversation history up to <see cref="ThroughSequence"/> (inclusive).
/// The sequence is the only replay boundary: everything after it is replayed verbatim, everything up to it
/// is represented by <see cref="Summary"/>.
/// </summary>
public sealed record ConversationCompactionRecord(
    Guid Id,
    Guid ConversationId,
    Guid TurnId,
    long ThroughSequence,
    string Summary,
    string? Model,
    int SourceMessageCount,
    int? SourceTokens,
    int? SummaryTokens,
    DateTimeOffset CreatedAtUtc);
