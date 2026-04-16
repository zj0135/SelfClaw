namespace SelfClaw.Core.Runtime;

public sealed record ExecutionPlanPreparedEvent(
    ExecutionPlan Plan) : ChatRuntimeEvent;
