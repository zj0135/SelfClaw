namespace SelfClaw.Core.Models;

/// <summary>
/// Application-wide usage overview for the About dashboard. <see cref="Lifetime"/> covers the whole
/// database and is unaffected by <see cref="UsageRange"/>; the range fields cover the requested
/// window only. <see cref="TimeZone"/> is the zone the local-day buckets were computed in.
/// </summary>
public sealed record UsageOverview(
    DateTimeOffset GeneratedAt,
    string TimeZone,
    UsageRange Range,
    UsageLifetimeSnapshot Lifetime,
    IReadOnlyList<UsageDailyPoint> RangeDaily,
    IReadOnlyList<UsageModelUsage> RangeModels,
    IReadOnlyList<UsageToolUsage> RangeTools);
