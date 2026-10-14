using SelfClaw.Core.Interfaces;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct.Models;

/// <summary>
/// How one Direct turn finished its setup. Setup either produces the provider client and messages the turn
/// needs to stream, or it throws before any client exists.
/// </summary>
internal abstract record DirectTurnSetup(DirectTurnCapabilityLease CapabilityLease)
{
    /// <summary>The turn may call the provider with <see cref="Messages"/> and the given tool seam.</summary>
    internal sealed record Ready(
        DirectTurnCapabilityLease CapabilityLease,
        AiChatClientLease ProviderLease,
        DirectToolInvoker Invoker,
        IReadOnlyList<ChatMessage> Messages,
        IDirectTurnInputSession? InputSession = null)
        : DirectTurnSetup(CapabilityLease);
}
