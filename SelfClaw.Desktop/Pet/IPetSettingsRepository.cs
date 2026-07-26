namespace SelfClaw.Desktop.Pet;

internal interface IPetSettingsRepository
{
    Task<PetSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(PetSettings settings, CancellationToken cancellationToken = default);
}
