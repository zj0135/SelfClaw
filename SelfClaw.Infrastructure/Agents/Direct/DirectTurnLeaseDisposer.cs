using Microsoft.Extensions.Logging;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// Releases the two leases one turn owns, in the order it acquired them. A setup that fails after one of them
/// exists and a turn that ends normally both go through here, so neither path can leak a provider pipeline or a
/// capability lease, and a failing release never masks the turn's own outcome.
/// </summary>
internal static class DirectTurnLeaseDisposer
{
    internal static async Task DisposeAsync(
        AiChatClientLease? providerLease,
        DirectTurnCapabilityLease capabilityLease,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(capabilityLease);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            providerLease?.Dispose();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to dispose the Direct AI chat client pipeline.");
        }

        try
        {
            await capabilityLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to dispose the Direct turn capability lease.");
        }
    }
}
