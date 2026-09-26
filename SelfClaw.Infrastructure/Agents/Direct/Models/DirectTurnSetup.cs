using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Models;

/// <summary>
/// The result of preparing one Direct turn. A null <see cref="ProviderLease"/> means the turn was
/// blocked (by an inherited hook Plugin or by <c>runStarting</c>) before any model client existed.
/// </summary>
internal sealed record DirectTurnSetup(
    DirectTurnCapabilityLease CapabilityLease,
    AiChatClientLease? ProviderLease,
    DirectToolInvoker? Invoker,
    IReadOnlyList<ChatMessage> Messages,
    string? BlockReason);
