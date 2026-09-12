namespace SelfClaw.Core.Models;

public sealed record SubagentActivityPage(
    SubagentActivityCounts Counts,
    string ListVersion,
    IReadOnlyList<SubagentActivityTask> Tasks,
    string? NextCursor);
