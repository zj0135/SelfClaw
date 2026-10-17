using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// SourceTokens is counted by IDirectTokenCounter and passed in; the compressor never estimates.
internal sealed record DirectCompressionRequest(
    IReadOnlyList<DirectPromptHistoryUnit> PrefixUnits,
    string? PreviousSummary,
    int SourceTokens,
    Guid? ModelProfileId,
    AiChatClientLease? TurnLease,
    Guid ConversationId,
    Guid TurnId);
