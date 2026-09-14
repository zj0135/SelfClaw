using SelfClaw.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Pet;

internal sealed class InMemoryPetSettingsRepository : IPetSettingsRepository
{
    public InMemoryPetSettingsRepository(PetSettings settings)
    {
        Settings = settings;
    }

    public PetSettings Settings { get; private set; }

    public int SaveCount { get; private set; }
    public Exception? SaveError { get; set; }

    public Task<PetSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Settings);
    }

    public Task SaveAsync(PetSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SaveError is not null) throw SaveError;
        Settings = settings;
        SaveCount++;
        return Task.CompletedTask;
    }
}
