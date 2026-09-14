namespace SelfClaw.Desktop.Pet;

internal interface IPetWindowAdapter
{
    event EventHandler<PetPlacement>? PlacementCommitted;

    Task<bool> GetIsVisibleAsync(CancellationToken cancellationToken = default);

    Task ShowAsync(PetSettings settings, PetLoadedPackage package, CancellationToken cancellationToken = default);

    Task HideAsync(CancellationToken cancellationToken = default);

    Task ReloadAsync(PetSettings settings, PetLoadedPackage package, CancellationToken cancellationToken = default);

    Task FlushPlacementAsync(CancellationToken cancellationToken = default);
}
