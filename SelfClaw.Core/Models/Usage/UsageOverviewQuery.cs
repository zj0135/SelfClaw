namespace SelfClaw.Core.Models;

/// <summary>
/// Time zone is supplied by the caller because "today", the range window and the streak metrics are
/// all local-day questions, and the host process may have been started before the user changed it.
/// </summary>
public sealed record UsageOverviewQuery(UsageRange Range, TimeZoneInfo TimeZone);
