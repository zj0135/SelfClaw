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
    string? SystemText,
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
    ToolImageDelivery ImageDelivery = ToolImageDelivery.Unsupported);
