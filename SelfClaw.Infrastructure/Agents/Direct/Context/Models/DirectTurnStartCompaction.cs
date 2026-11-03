using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// What one turn-start compaction decided: the boundary the prompt should replay against (the stored record,
// unchanged when nothing was summarized), the measurement of the prompt that carries it (null when counting is
// unavailable or nothing was worth counting) and the plan section that prompt was counted with.
internal sealed record DirectTurnStartCompaction(
    ConversationCompactionRecord? Compression,
    DirectCountedPrompt? Counted,
    string? TodoSection);
