namespace SelfClaw.Core.Runtime;

public sealed record ExecutionPlan(
    string? Summary,
    IReadOnlyList<ExecutionPlanStep> Steps);
