using SelfClaw.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Pet;

internal sealed class FakePetWindowAdapter : IPetWindowAdapter
{
    public event EventHandler<PetPlacement>? PlacementCommitted;

    public bool IsVisible { get; private set; }

    public int ShowCount { get; private set; }

    public int HideCount { get; private set; }

    public List<PetSettings> ReloadedSettings { get; } = [];

    public TaskCompletionSource<bool>? ShowStarted { get; set; }

    public TaskCompletionSource<bool>? ContinueShow { get; set; }

    public Task<bool> GetIsVisibleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsVisible);
    }

    public async Task ShowAsync(PetSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ShowCount++;
        ShowStarted?.TrySetResult(true);
        if (ContinueShow is not null)
        {
            await ContinueShow.Task.WaitAsync(cancellationToken);
        }

        IsVisible = true;
    }

    public Task HideAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HideCount++;
        IsVisible = false;
        return Task.CompletedTask;
    }

    public Task ReloadAsync(PetSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        ReloadedSettings.Add(settings);
        return Task.CompletedTask;
    }

    public void CommitPlacement(PetPlacement placement)
        => PlacementCommitted?.Invoke(this, placement);
}
