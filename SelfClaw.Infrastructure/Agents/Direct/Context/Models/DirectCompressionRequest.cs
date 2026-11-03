using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// SourceTokens is counted by IDirectTokenCounter and passed in; the compressor never estimates.
// TurnLease is the lease of the turn being compressed: it is the fallback summarizer whenever the policy's
// dedicated model is unusable, so the compressor never has to degrade to "no summary" for lack of a client.
internal sealed record DirectCompressionRequest(
    IReadOnlyList<DirectPromptHistoryUnit> PrefixUnits,
    string? PreviousSummary,
    int SourceTokens,
    Guid? ModelProfileId,
    AiChatClientLease TurnLease,
    Guid ConversationId,
    Guid TurnId);
