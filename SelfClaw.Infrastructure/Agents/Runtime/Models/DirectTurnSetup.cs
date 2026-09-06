using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.Extensions.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime.Models;

internal sealed record DirectTurnSetup(
    DirectTurnCapabilityLease CapabilityLease,
    AiChatClientLease ProviderLease,
    IReadOnlyList<ChatMessage> Messages);
