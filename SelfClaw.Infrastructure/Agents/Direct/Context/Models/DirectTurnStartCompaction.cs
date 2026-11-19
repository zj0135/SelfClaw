using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// What one turn-start compaction decided: the boundary the prompt should replay against (the stored record,
// unchanged when nothing was summarized), the measurement of the prompt that carries it (null when counting is
// unavailable or nothing was worth counting), and the system text and plan section that prompt was counted with.
// The turn sends exactly these two, so the request that was sized is the request that is sent.
internal sealed record DirectTurnStartCompaction(
    ConversationCompactionRecord? Compression,
    DirectCountedPrompt? Counted,
    string? SystemText,
    string? TodoSection);
