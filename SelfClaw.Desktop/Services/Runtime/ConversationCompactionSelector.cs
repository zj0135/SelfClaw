using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Which persisted summary a turn must replay from. The boundary is the only truth: choosing by timestamp
/// would let an older boundary win and replay messages a newer compaction already summarized.
/// </summary>
internal static class ConversationCompactionSelector
{
    internal static ConversationCompactionRecord? SelectLatest(
        IReadOnlyList<ConversationCompactionRecord>? compactions)
        => compactions is not { Count: > 0 }
            ? null
            : compactions
                .OrderByDescending(compaction => compaction.ThroughSequence)
                .ThenByDescending(compaction => compaction.CreatedAtUtc)
                .ThenByDescending(compaction => compaction.Id)
                .First();
}
