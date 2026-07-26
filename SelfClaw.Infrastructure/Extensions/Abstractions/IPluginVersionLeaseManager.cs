using SelfClaw.Infrastructure.Extensions;

namespace SelfClaw.Infrastructure.Extensions.Abstractions;

internal interface IPluginVersionLeaseManager
{
    PluginVersionLease Acquire(string installPath);

    Task<PluginVersionDrainLease> AcquireDrainsAsync(
        IReadOnlyList<string> installPaths,
        CancellationToken cancellationToken = default);

    Task DrainAsync(string installPath, CancellationToken cancellationToken = default);
}
