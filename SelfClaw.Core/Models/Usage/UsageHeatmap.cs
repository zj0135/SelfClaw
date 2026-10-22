namespace SelfClaw.Core.Models;

/// <summary>
/// Consecutive local days with no gaps: <see cref="Days"/> starts at <see cref="StartDate"/> and ends
/// at <see cref="EndDate"/>, so the renderer can index it without filling gaps itself.
/// <see cref="MaxTokens"/> is the daily peak inside this window; the renderer recomputes levels per
/// heatmap mode because a daily peak and a weekly peak are different scales.
/// </summary>
public sealed record UsageHeatmap(
    DateOnly StartDate,
    DateOnly EndDate,
    long MaxTokens,
    IReadOnlyList<UsageHeatmapDay> Days);
