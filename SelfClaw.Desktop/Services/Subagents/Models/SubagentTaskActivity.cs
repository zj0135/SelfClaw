using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentTaskActivity(
    SubagentActivityTask Task,
    string Phase,
    int PendingApprovalCount,
    string? RecordingError);
