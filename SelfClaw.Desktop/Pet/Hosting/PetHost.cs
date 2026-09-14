using Microsoft.Extensions.Logging;

namespace SelfClaw.Desktop.Pet;

public sealed class PetHost : IAsyncDisposable
{
    private readonly IPetSettingsRepository _settingsRepository;
    private readonly IPetWindowAdapter _windowAdapter;
    private readonly PetPackageCatalog _packageCatalog;
    private readonly ILogger<PetHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PetSettings _settings = new();
    private bool _loaded;
    private bool _initialized;
    private (PetSettings Settings, PetLoadedPackage Package)? _prepared;
    private IReadOnlyList<PetPackageSummary> _builtInPackages = [];
    private string _loadStatus = "unloaded";
    private string? _loadError;
    private long _stateRevision;
    private readonly object _placementGate = new();
    private Task _placementSaveTask = Task.CompletedTask;
    private bool _stopping;

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

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_settings.Enabled)
            {
                await ShowAsync(cancellationToken).ConfigureAwait(false);
            }
            _initialized = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { RecordFailure(exception); throw; }
        finally
        {
            _gate.Release();
            NotifyChanged();
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
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { RecordFailure(exception); throw; }
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
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { RecordFailure(exception); throw; }
        finally
        {
            _gate.Release();
            NotifyChanged();
        }
    }

    private async Task ShowAsync(CancellationToken cancellationToken)
    {
        var package = _prepared is { } cached && cached.Settings.SpriteSheetPath == _settings.SpriteSheetPath &&
            Equals(cached.Settings.Grid, _settings.Grid) ? cached.Package :
            await _packageCatalog.LoadAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (!_settings.Enabled)
        {
            await PersistAsync(_settings with { Enabled = true }, cancellationToken).ConfigureAwait(false);
        }
        await _windowAdapter.ShowAsync(_settings, package, cancellationToken).ConfigureAwait(false);
        AcceptPackage(package);
    }

    private async Task HideAsync(CancellationToken cancellationToken)
    {
        if (_settings.Enabled)
        {
            await PersistAsync(_settings with { Enabled = false }, cancellationToken).ConfigureAwait(false);
        }
        await _windowAdapter.HideAsync(cancellationToken).ConfigureAwait(false);
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

        var prepared = await _packageCatalog.LoadAsync(next, cancellationToken).ConfigureAwait(false);
        await PersistAsync(next, cancellationToken).ConfigureAwait(false);
        await _windowAdapter.ReloadAsync(next, prepared, cancellationToken).ConfigureAwait(false);
        AcceptPackage(prepared);
    }

    private async Task<PetHostState> CreateStateAsync(CancellationToken cancellationToken)
    {
        var isVisible = await _windowAdapter.GetIsVisibleAsync(cancellationToken).ConfigureAwait(false);
        return new PetHostState(
            isVisible,
            _settings,
            _packageCatalog.ResolveSelectedBuiltInPetId(_settings.SpriteSheetPath),
            _builtInPackages,
            _prepared?.Package.PackageId,
            _loadStatus,
            _loadError,
            ++_stateRevision);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _settings = await _settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        _builtInPackages = await _packageCatalog.GetBuiltInPackagesAsync(cancellationToken).ConfigureAwait(false);
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
        lock (_placementGate)
        {
            if (_stopping) return;
            _placementSaveTask = SavePlacementAfterAsync(_placementSaveTask, placement);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _windowAdapter.FlushPlacementAsync(cancellationToken).ConfigureAwait(false);
        Task pending;
        lock (_placementGate)
        {
            _stopping = true;
            _windowAdapter.PlacementCommitted -= OnPlacementCommitted;
            pending = _placementSaveTask;
        }
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void AcceptPackage(PetLoadedPackage package)
    {
        _prepared = (_settings, package);
        _loadStatus = package.Warning is null ? "ready" : "fallback";
        _loadError = package.Warning;
    }

    private void RecordFailure(Exception exception)
    {
        _loadStatus = "failed";
        _loadError = exception.Message;
        _logger.LogWarning(exception, "Pet configuration or resource installation failed.");
    }

    private void NotifyChanged()
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action>() ?? [])
        {
            try { handler(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { _logger.LogError(exception, "Failed to publish pet state."); }
        }
    }

    private async Task SavePlacementAfterAsync(Task previous, PetPlacement placement)
    {
        await previous.ConfigureAwait(false);
        await PersistPlacementAsync(placement).ConfigureAwait(false);
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
