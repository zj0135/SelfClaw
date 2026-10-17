using SelfClaw.Core.Interfaces;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct.Models;

/// <summary>
/// One Direct turn's prepared state: the provider client, the tool seam and the messages the loop streams,
/// plus the leases the runtime disposes once the turn is over. Setup either produces it or throws before any
/// client exists.
/// </summary>
internal sealed record DirectTurnSetup(
    DirectTurnCapabilityLease CapabilityLease,
    AiChatClientLease ProviderLease,
    DirectToolInvoker Invoker,
    IReadOnlyList<ChatMessage> Messages,
    IDirectTurnInputSession? InputSession = null);
