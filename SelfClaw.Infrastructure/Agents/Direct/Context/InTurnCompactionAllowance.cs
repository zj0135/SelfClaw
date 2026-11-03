namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// How many in-turn compactions one turn may attempt. A failed attempt counts too: the cap bounds the latency
/// and cost a summary can add to a single turn, not only the successful summaries. It lives beside the turn
/// that owns it, never on the singleton that orchestrates compaction.
/// </summary>
internal sealed class InTurnCompactionAllowance
{
    private int _attempts;

    internal bool IsExhausted => _attempts >= DirectCompactionStep.MaximumInTurnCompactionsPerTurn;

    internal void RecordAttempt() => _attempts++;
}
