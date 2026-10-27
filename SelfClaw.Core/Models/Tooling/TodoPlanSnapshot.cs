namespace SelfClaw.Core.Models;

/// <summary>
/// A resolved task plan together with the call that produced it. <see cref="Run"/> is the same instance the
/// caller passed in, so a display layer can key its own cache on it instead of re-looking it up by id.
/// <see cref="SourceSequence"/> is the sequence of the message that carried the call: a push summary covering
/// that sequence is what makes the plan unreplayable from history.
/// </summary>
public sealed record TodoPlanSnapshot(
    TodoPlan Plan,
    ToolExecutionRecord Run,
    long SourceSequence);
