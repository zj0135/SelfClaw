namespace SelfClaw.Core.Models;

public sealed record SubagentCompletionBatch(
    IReadOnlyList<SubagentCompletionEnvelope> Deliveries);
