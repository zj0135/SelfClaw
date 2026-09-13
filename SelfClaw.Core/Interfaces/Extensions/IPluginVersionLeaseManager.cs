namespace SelfClaw.Core.Interfaces;

public interface IPluginVersionLeaseManager
{
    IDisposable Acquire(string installPath);

    Task<IDisposable> AcquireDrainsAsync(
        IReadOnlyList<string> installPaths,
        CancellationToken cancellationToken = default);

    Task DrainAsync(string installPath, CancellationToken cancellationToken = default);
}
