using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// One unit is exactly one stored message. A unit never splits a tool call from its result, which is why
// compression boundaries may only fall between units. Tokens carries the IDirectTokenCounter result for the
// unit and stays null until the caller counts it. ContainsAttachments marks a unit the compaction planner must
// never fold into a summary: a summary cannot represent an image.
internal sealed record DirectPromptHistoryUnit(
    IReadOnlyList<ChatMessage> Messages,
    long SourceSequence,
    int? Tokens = null,
    bool ContainsAttachments = false);
