namespace SelfClaw.Infrastructure.Agents.Direct.Models;

internal enum DirectLoopOutcome
{
    Completed,
    Length,
    ContentFiltered,
    BudgetExhausted,
    ContextBudgetExceeded,

    /// <summary>
    /// The turn kept repeating the same tool calls with the same results. Distinct from
    /// <see cref="BudgetExhausted"/> so the transcript can say why it stopped: nothing was progressing,
    /// rather than the request limit being reached.
    /// </summary>
    Stalled
}
