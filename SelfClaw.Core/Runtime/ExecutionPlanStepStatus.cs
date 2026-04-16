namespace SelfClaw.Core.Runtime;

public enum ExecutionPlanStepStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}
