using SelfClaw.Core.Interfaces;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Models;

/// <summary>
/// One Direct turn's prepared state: the provider client, the tool seam and the structured prompt the loop
/// streams, plus the compression settings that bound what the loop may do mid-turn, plus the leases the
/// runtime disposes once the turn is over. Setup either produces it or throws before any client exists.
/// </summary>
internal sealed record DirectTurnSetup(
    DirectTurnCapabilityLease CapabilityLease,
    AiChatClientLease ProviderLease,
    DirectToolInvoker Invoker,
    /// <summary>
    /// The system sections that stay byte-identical for the whole turn. The summary and the plan are kept
    /// apart because an in-turn compaction replaces them.
    /// </summary>
    string? FixedSystemText,
    IReadOnlyList<DirectPromptHistoryUnit> InitialUnits,
    IReadOnlyList<ChatMessage> TrailingProtectedMessages,
    DirectCompressionPolicy CompressionPolicy,
    DirectPromptBudget Budget,
    Guid ConversationId,
    Guid TurnId,
    IDirectTurnInputSession? InputSession = null,
    /// <summary>
    /// How this turn's protocol carries the images a tool result produced. The loop owns the two shapes,
    /// so a tool never needs to know which provider is on the other end.
    /// </summary>
    ToolImageDelivery ImageDelivery = ToolImageDelivery.Unsupported,
    /// <summary>
    /// The plan section the turn starts with. Null when the turn start injected none.
    /// </summary>
    string? PlanSection = null,
    /// <summary>
    /// The summary the turn starts with: the one of the boundary <see cref="InitialUnits"/> replay against.
    /// </summary>
    string? Summary = null,
    /// <summary>
    /// The highest message sequence stored when the turn started. Once an in-turn compaction has covered every
    /// initial unit, this is how far the stored history is covered.
    /// </summary>
    long HistoryThroughSequence = 0);
