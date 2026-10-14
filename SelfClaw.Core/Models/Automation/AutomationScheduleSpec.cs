namespace SelfClaw.Core.Models;

/// <summary>
/// Trigger description for an <see cref="AutomationTaskRecord"/>. <see cref="Weekdays"/> uses the cron
/// convention (0 = Sunday .. 6 = Saturday) so no conversion is needed when composing an expression.
/// </summary>
public sealed record AutomationScheduleSpec(
    int Version = 1,
    string? Time = null,
    IReadOnlyList<int>? Weekdays = null,
    int? DayOfMonth = null,
    DateTimeOffset? RunAtUtc = null,
    string? Cron = null);
