namespace SelfClaw.Core.Models;

public sealed record SubagentActivityCounts(
    int Total,
    int Queued,
    int Running,
    int Succeeded,
    int Failed,
    int Cancelled,
    int Interrupted);
