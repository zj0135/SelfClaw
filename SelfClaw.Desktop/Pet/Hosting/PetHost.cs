using Microsoft.Extensions.Logging;

namespace SelfClaw.Desktop.Pet;

public sealed class PetHost
{
    private readonly IPetSettingsRepository _settingsRepository;
    private readonly IPetWindowAdapter _windowAdapter;
    private readonly PetPackageCatalog _packageCatalog;
    private readonly ILogger<PetHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PetSettings _settings = new();
    private bool _loaded;

    internal PetHost(
        IPetSettingsRepository settingsRepository,
        IPetWindowAdapter windowAdapter,
        PetPackageCatalog packageCatalog,
        ILogger<PetHost> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsRepository);
        ArgumentNullException.ThrowIfNull(windowAdapter);
        ArgumentNullException.ThrowIfNull(packageCatalog);
        ArgumentNullException.ThrowIfNull(logger);

        _settingsRepository = settingsRepository;
        _windowAdapter = windowAdapter;
        _packageCatalog = packageCatalog;
        _logger = logger;
        _windowAdapter.PlacementCommitted += OnPlacementCommitted;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_settings.Enabled)
            {
                await _windowAdapter.ShowAsync(_settings, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PetHostState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return await CreateStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PetHostState> ExecuteAsync(
        PetHostCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            switch (command.Kind)
            {
                case PetHostCommandKind.Show:
                    await ShowAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case PetHostCommandKind.Hide:
                    await HideAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case PetHostCommandKind.Toggle:
                    await ToggleAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case PetHostCommandKind.SelectBuiltInPet:
                    await SelectBuiltInPetAsync(command.PetId, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unknown pet host command.");
            }

            return await CreateStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ShowAsync(CancellationToken cancellationToken)
    {
        await _windowAdapter.ShowAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (!_settings.Enabled)
        {
            await PersistAsync(_settings with { Enabled = true }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HideAsync(CancellationToken cancellationToken)
    {
        await _windowAdapter.HideAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.Enabled)
        {
            await PersistAsync(_settings with { Enabled = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ToggleAsync(CancellationToken cancellationToken)
    {
        if (await _windowAdapter.GetIsVisibleAsync(cancellationToken).ConfigureAwait(false))
        {
            await HideAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await ShowAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SelectBuiltInPetAsync(string? petId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(petId);

        var package = _packageCatalog.GetBuiltInPackage(petId);
        var next = _settings with
        {
            SpriteSheetPath = package.Id,
            Grid = null,
        };

        await PersistAsync(next, cancellationToken).ConfigureAwait(false);
        await _windowAdapter.ReloadAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PetHostState> CreateStateAsync(CancellationToken cancellationToken)
    {
        var isVisible = await _windowAdapter.GetIsVisibleAsync(cancellationToken).ConfigureAwait(false);
        return new PetHostState(
            isVisible,
            _settings,
            _packageCatalog.ResolveSelectedBuiltInPetId(_settings.SpriteSheetPath),
            _packageCatalog.GetBuiltInPackages());
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _settings = await _settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;
    }

    private async Task PersistAsync(PetSettings next, CancellationToken cancellationToken)
    {
        await _settingsRepository.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        _settings = next;
        _loaded = true;
    }

    private void OnPlacementCommitted(object? sender, PetPlacement placement)
    {
        _ = PersistPlacementAsync(placement);
    }

    private async Task PersistPlacementAsync(PetPlacement placement)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(false);
                await PersistAsync(
                    _settings with
                    {
                        OffsetX = placement.OffsetX,
                        OffsetY = placement.OffsetY,
                        ScreenDeviceName = placement.ScreenDeviceName,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist pet placement.");
        }
    }
}
