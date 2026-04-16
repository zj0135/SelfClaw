namespace SelfClaw.Core.Runtime;

public sealed record ExecutionPlanStep(
    string Id,
    string Title,
    ExecutionPlanStepStatus Status = ExecutionPlanStepStatus.Pending);
