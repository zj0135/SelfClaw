namespace SelfClaw.Core.Models;

/// <summary>Aggregated tool calls over the range window. <see cref="AvgDurationMs"/> is null when no
/// call in the group reported a duration.</summary>
public sealed record UsageToolUsage(
    string ToolName,
    string DisplayName,
    int CallCount,
    int ErrorCount,
    double ErrorRate,
    double? AvgDurationMs);
