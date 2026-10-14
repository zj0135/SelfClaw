namespace SelfClaw.Core.Models;

/// <summary>
/// Rough on-disk size of retained automation transcripts. Computed with SQL LENGTH() so it can be shown
/// without loading message bodies; it is an observation aid, not an accounting figure.
/// </summary>
public sealed record AutomationHistoryUsage(IReadOnlyList<AutomationTaskHistoryUsage> Tasks, long TotalBytes);
