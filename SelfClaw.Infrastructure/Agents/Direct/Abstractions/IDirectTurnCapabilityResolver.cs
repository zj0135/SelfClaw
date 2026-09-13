using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Abstractions;

internal interface IDirectTurnCapabilityResolver
{
    Task<DirectTurnCapabilityLease> ResolveAsync(
        DirectChatTurnRequest request,
        CancellationToken cancellationToken = default);
}
