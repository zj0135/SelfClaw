namespace SelfClaw.Core.Models;

/// <summary>
/// A single automation calendar entry. <see cref="Kind"/> is one of <c>projected</c>, <c>todo</c> or <c>run</c>.
/// </summary>
public sealed record AutomationCalendarEvent(
    string Kind,
    Guid TaskId,
    DateTimeOffset AtUtc,
    bool AllDay,
    string Title,
    Guid? RunId = null,
    string? Status = null,
    bool IsOverdue = false,
    long? DurationMs = null,
    long? Tokens = null);
