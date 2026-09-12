namespace SelfClaw.Core.Models;

public enum SubagentStateChangeKind
{
    Task,
    Delivery,
    ExecutionContent,
    ExecutionBoundary,
    Approval,
    ScopeClosed,
    ScopeOpened
}
