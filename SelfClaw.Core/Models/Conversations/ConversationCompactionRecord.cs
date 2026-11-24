namespace SelfClaw.Core.Models;

/// <summary>
/// A persisted summary that replaces the conversation history up to <see cref="ThroughSequence"/> (inclusive):
/// everything after the boundary is replayed verbatim, everything up to it is represented by
/// <see cref="Summary"/>. A turn stores all of its assistant output in one message, so a summary written while
/// that turn was still running reaches into the message after the boundary. <see cref="ThroughToolRunId"/>
/// names the last tool call of that message the summary covers; the calls before it are represented by the
/// summary too, and only what follows it is replayed. An anchor that no longer resolves is treated as absent,
/// which replays content the summary already holds but never drops any.
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
    DateTimeOffset CreatedAtUtc,
    Guid? ThroughToolRunId = null);
