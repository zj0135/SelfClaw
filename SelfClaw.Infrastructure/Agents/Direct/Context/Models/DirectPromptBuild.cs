using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// One Direct request decomposed into the three zones of design §4: the system text, the replayable units
// in order, and the protected messages that must stay after them. Flattening it yields the ChatMessage
// list the provider receives, so the composition order never has to be re-derived by a second code path.
internal sealed record DirectPromptBuild(
    string? SystemText,
    IReadOnlyList<DirectPromptHistoryUnit> Units,
    IReadOnlyList<ChatMessage> TrailingProtectedMessages);
