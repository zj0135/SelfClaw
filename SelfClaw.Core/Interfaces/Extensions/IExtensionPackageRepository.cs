using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IExtensionPackageRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExtensionPackageRecord>> ListPackagesAsync(CancellationToken cancellationToken = default);

    Task<ExtensionPackageRecord?> GetPackageAsync(
        ExtensionKind kind,
        string id,
        CancellationToken cancellationToken = default);

    Task<ExtensionPackageRecord> UpsertPackageAsync(
        ExtensionPackageRecord package,
        CancellationToken cancellationToken = default);

    Task SetPackageEnabledAsync(
        ExtensionKind kind,
        string id,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task DeletePackageAsync(
        ExtensionKind kind,
        string id,
        CancellationToken cancellationToken = default);
}
