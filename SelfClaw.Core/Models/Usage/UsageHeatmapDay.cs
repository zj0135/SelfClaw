namespace SelfClaw.Core.Models;

/// <summary>One local day of the activity heatmap; <see cref="Date"/> is a local calendar date.</summary>
public sealed record UsageHeatmapDay(DateOnly Date, long TotalTokens, int TurnCount, int ToolCallCount);
