namespace SelfClaw.Core.Runtime;

public sealed record ExecutionPlanStepStatusChangedEvent(
    string StepId,
    ExecutionPlanStepStatus Status) : ChatRuntimeEvent;
