using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Extensions.Runtime;

namespace SelfClaw.Infrastructure.Extensions.Abstractions;

internal interface IDirectTurnCapabilityResolver
{
    Task<DirectTurnCapabilityLease> ResolveAsync(
        DirectChatTurnRequest request,
        CancellationToken cancellationToken = default);
}
