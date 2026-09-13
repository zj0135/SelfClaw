using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentActivityPageSnapshot(
    SubagentActivityCounts Counts,
    string ListVersion,
    IReadOnlyList<SubagentTaskActivity> Activities,
    string? NextCursor);
