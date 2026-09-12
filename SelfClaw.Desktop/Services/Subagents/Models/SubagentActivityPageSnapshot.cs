using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentActivityPageSnapshot(
    SubagentActivityPage Page,
    IReadOnlyList<SubagentTaskActivity> Activities,
    long ObservationRevision);
