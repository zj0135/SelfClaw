namespace SelfClaw.Core.Models;

/// <summary>Usage aggregated for one model within the reported scope.</summary>
public sealed record ModelUsageSummary(
    string Model,
    UsageTotals Totals,
    double? CacheHitRate);
