namespace SelfClaw.Core.Models;

/// <summary>
/// Usage reported for one scope (a conversation, or the Subagent tasks owned by a parent conversation).
/// <see cref="CacheHitRate"/> is <c>cached / (cached + uncached)</c>; null when nothing was reported.
/// </summary>
public sealed record UsageReport(
    UsageTotals Totals,
    double? CacheHitRate,
    IReadOnlyList<ModelUsageSummary> Models);
