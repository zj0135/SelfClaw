using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct.Models;

/// <summary>
/// How one Direct turn finished its setup. There are exactly two outcomes: either a provider client exists
/// and the turn may stream, or setup refused it before any client existed and only the reason remains to be
/// reported. Making them separate cases keeps the runtime from treating a null client as "blocked".
/// </summary>
internal abstract record DirectTurnSetup(DirectTurnCapabilityLease CapabilityLease)
{
    /// <summary>
    /// The inherited hook policy or a <c>runStarting</c> hook refused the turn; the Blocked terminal event is
    /// already written. No invoker exists because no tool can have been called.
    /// </summary>
    internal sealed record Blocked(DirectTurnCapabilityLease CapabilityLease, string? Reason)
        : DirectTurnSetup(CapabilityLease);

    /// <summary>The turn may call the provider with <see cref="Messages"/> and the given tool seam.</summary>
    internal sealed record Ready(
        DirectTurnCapabilityLease CapabilityLease,
        AiChatClientLease ProviderLease,
        DirectToolInvoker Invoker,
        IReadOnlyList<ChatMessage> Messages)
        : DirectTurnSetup(CapabilityLease);
}
