using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IProfileRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderProfile>> ListProfilesAsync(CancellationToken cancellationToken = default);

    Task<ProviderProfile?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<ProviderProfile> UpsertProfileAsync(ProviderProfile profile, CancellationToken cancellationToken = default);

    Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
}