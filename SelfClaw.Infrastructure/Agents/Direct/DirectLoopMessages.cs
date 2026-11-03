namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// The turn-ending stop messages, defined once. The loop publishes the per-call detail when it cancels
/// calls that never ran and the runtime publishes the terminal message, so the transcript and the terminal
/// event can never describe the same stop differently.
/// </summary>
internal static class DirectLoopMessages
{
    internal const string BudgetExhaustedReason = "BudgetExhausted";
    internal const string BatchAbortedReason = "BatchAborted";

    private static readonly string RequestLimitPhrase =
        $"reached its {DirectTurnRun.MaximumProviderRequestsPerTurn} provider request limit";

    private static readonly string RepeatedBatchPhrase =
        "repeated the same tool calls with the same results " +
        $"{NoProgressGuard.RepeatedBatchLimit} times in a row and was stopped to avoid an endless loop.";

    internal static readonly string BudgetExhaustedCanceledCall =
        $"The turn {RequestLimitPhrase} before this tool could execute.";

    internal static readonly string BudgetExhaustedTerminal =
        $"{BudgetExhaustedReason}: the turn {RequestLimitPhrase} before it could continue.";

    internal static readonly string ContextBudgetExceededTerminal =
        "ContextBudgetExceeded: the queued steer could not fit in the model context window; the unconsumed input was held.";

    internal static readonly string BatchAbortedCall =
        "The tool was not executed because an earlier tool call of the same batch aborted the turn.";

    internal static readonly string NoProgressTerminal =
        $"NoProgress: the turn {RepeatedBatchPhrase} Tell the agent what to do differently and try again.";
}
