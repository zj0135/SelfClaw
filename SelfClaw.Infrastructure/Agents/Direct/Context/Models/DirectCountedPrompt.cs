namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// The turn-start counterpart of DirectPromptMeasurement: the same two sizes plus every replayed unit carrying
// its own count, which is what the compaction planner sizes a prefix with. Units an existing summary already
// covers keep a null Tokens because they never reach the request.
internal sealed record DirectCountedPrompt(
    int PromptTokens,
    int TotalWithReserve,
    IReadOnlyList<DirectPromptHistoryUnit> Units);
