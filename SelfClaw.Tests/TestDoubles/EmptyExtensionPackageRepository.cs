using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class EmptyExtensionPackageRepository : IExtensionPackageRepository
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ExtensionPackageRecord>> ListPackagesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ExtensionPackageRecord>>([]);

    public Task<ExtensionPackageRecord?> GetPackageAsync(
        ExtensionKind kind,
        string id,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ExtensionPackageRecord?>(null);

    public Task<ExtensionPackageRecord> UpsertPackageAsync(
        ExtensionPackageRecord package,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetPackageEnabledAsync(
        ExtensionKind kind,
        string id,
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task DeletePackageAsync(
        ExtensionKind kind,
        string id,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
