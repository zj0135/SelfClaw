namespace SelfClaw.Desktop.Pet;

internal interface IPetWindowAdapter
{
    event EventHandler<PetPlacement>? PlacementCommitted;

    Task<bool> GetIsVisibleAsync(CancellationToken cancellationToken = default);

    Task ShowAsync(PetSettings settings, CancellationToken cancellationToken = default);

    Task HideAsync(CancellationToken cancellationToken = default);

    Task ReloadAsync(PetSettings settings, CancellationToken cancellationToken = default);
}
