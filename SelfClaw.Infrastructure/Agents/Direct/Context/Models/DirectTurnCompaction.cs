using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct.Context.Models;

// The turn-start compaction input: the units the turn is going to replay, the plan snapshot read from stored
// history, and the request/lease/budget/policy the decision and the summary call are made under. The fixed
// system text and the protected tail are the ones the turn sends, so what is counted here is what is sent.
internal sealed record DirectTurnCompaction(
    DirectChatTurnRequest Request,
    AiChatClientLease ProviderLease,
    DirectPromptBudget Budget,
    DirectCompressionPolicy Policy,
    IReadOnlyList<DirectPromptHistoryUnit> Units,
    TodoPlanSnapshot? PlanSnapshot,
    string? FixedSystemText,
    IReadOnlyList<ChatMessage> TrailingProtectedMessages);
