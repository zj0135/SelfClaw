using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace SelfClaw.Desktop.Pet;

internal sealed class WpfPetWindowAdapter : IPetWindowAdapter, IDisposable
{
    private static readonly TimeSpan PlacementDebounce = TimeSpan.FromMilliseconds(400);

    private readonly ILoggerFactory _loggerFactory;
    private readonly PetActivityPresenter _activityPresenter;
    private readonly PetPackageCatalog _packageCatalog;

    private PetWindow? _window;
    private bool _windowPetLoaded;
    private DispatcherTimer? _placementTimer;
    private Point _pendingPosition;
    private bool _disposed;

    public WpfPetWindowAdapter(
        ILoggerFactory loggerFactory,
        PetActivityPresenter activityPresenter,
        PetPackageCatalog packageCatalog)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(activityPresenter);
        ArgumentNullException.ThrowIfNull(packageCatalog);

        _loggerFactory = loggerFactory;
        _activityPresenter = activityPresenter;
        _packageCatalog = packageCatalog;
    }

    public event EventHandler<PetPlacement>? PlacementCommitted;

    public Task<bool> GetIsVisibleAsync(CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => _window is { IsVisible: true }, cancellationToken);

    public Task ShowAsync(PetSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return InvokeOnUiThreadAsync(
            () =>
            {
                var window = EnsureWindow();
                if (!_windowPetLoaded)
                {
                    window.LoadPet(settings);
                    _windowPetLoaded = true;
                }

                window.Show();
                RestoreWindowPosition(settings);
            },
            cancellationToken);
    }

    public Task HideAsync(CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => _window?.Hide(), cancellationToken);

    public Task ReloadAsync(PetSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return InvokeOnUiThreadAsync(
            () =>
            {
                if (_window is null)
                {
                    return;
                }

                _window.LoadPet(settings);
                _windowPetLoaded = true;
            },
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeUiResources();
    }

    private static Task InvokeOnUiThreadAsync(Action action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }

    private static Task<T> InvokeOnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }

    private PetWindow EnsureWindow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null)
        {
            return _window;
        }

        _window = new PetWindow(new PetViewModel(
            _loggerFactory.CreateLogger<PetViewModel>(),
            _activityPresenter,
            _packageCatalog));
        _window.PositionCommitted += OnPositionCommitted;
        _window.Closed += OnWindowClosed;
        return _window;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window is not null)
        {
            _window.PositionCommitted -= OnPositionCommitted;
            _window.Closed -= OnWindowClosed;
        }

        _window = null;
        _windowPetLoaded = false;
    }

    private void RestoreWindowPosition(PetSettings settings)
    {
        if (_window is null)
        {
            return;
        }

        var screen = ResolveScreen(settings.ScreenDeviceName);
        var (workAreaLeft, workAreaTop, workAreaWidth, workAreaHeight) =
            PhysicalWorkAreaToDip(screen.WorkingArea);

        var anchorLeft = settings.OffsetX is double offsetX
            ? workAreaLeft + offsetX
            : workAreaLeft + workAreaWidth - PetWindow.PetVisualWidth - 24d;
        var anchorTop = settings.OffsetY is double offsetY
            ? workAreaTop + offsetY
            : workAreaTop + workAreaHeight - PetWindow.PetVisualHeight - 24d;

        anchorLeft = Math.Clamp(
            anchorLeft,
            workAreaLeft,
            Math.Max(workAreaLeft, workAreaLeft + workAreaWidth - PetWindow.PetVisualWidth));
        anchorTop = Math.Clamp(
            anchorTop,
            workAreaTop,
            Math.Max(workAreaTop, workAreaTop + workAreaHeight - PetWindow.PetVisualHeight));

        _window.SetPetAnchorPosition(new Point(anchorLeft, anchorTop));
    }

    private void OnPositionCommitted(object? sender, Point position)
    {
        _pendingPosition = position;
        _placementTimer ??= new DispatcherTimer { Interval = PlacementDebounce };
        _placementTimer.Tick -= OnPlacementTimerTick;
        _placementTimer.Tick += OnPlacementTimerTick;
        _placementTimer.Stop();
        _placementTimer.Start();
    }

    private void OnPlacementTimerTick(object? sender, EventArgs e)
    {
        _placementTimer?.Stop();
        if (_window is null)
        {
            return;
        }

        var screen = ResolveScreenForWindow(_window);
        var (workAreaLeft, workAreaTop, _, _) = PhysicalWorkAreaToDip(screen.WorkingArea);
        PlacementCommitted?.Invoke(
            this,
            new PetPlacement(
                _pendingPosition.X - workAreaLeft,
                _pendingPosition.Y - workAreaTop,
                screen.DeviceName));
    }

    private static WinFormsScreen ResolveScreen(string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = Array.Find(
                WinFormsScreen.AllScreens,
                screen => string.Equals(screen.DeviceName, deviceName, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return WinFormsScreen.PrimaryScreen ?? WinFormsScreen.AllScreens[0];
    }

    private static WinFormsScreen ResolveScreenForWindow(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        var toDevice = source?.CompositionTarget?.TransformToDevice;
        var centerDip = new Point(
            window.Left + (window.Width / 2d),
            window.Top + (window.Height / 2d));
        var centerPhysical = toDevice is { } transform
            ? transform.Transform(centerDip)
            : centerDip;

        return WinFormsScreen.FromPoint(
            new System.Drawing.Point((int)centerPhysical.X, (int)centerPhysical.Y));
    }

    private (double Left, double Top, double Width, double Height) PhysicalWorkAreaToDip(
        System.Drawing.Rectangle workArea)
    {
        var source = _window is not null ? PresentationSource.FromVisual(_window) : null;
        var transform = source?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            return (workArea.Left, workArea.Top, workArea.Width, workArea.Height);
        }

        var topLeft = transform.Value.Transform(new Point(workArea.Left, workArea.Top));
        var size = transform.Value.Transform(new Vector(workArea.Width, workArea.Height));
        return (topLeft.X, topLeft.Y, size.X, size.Y);
    }

    private void DisposeUiResources()
    {
        var dispatcher = Application.Current?.Dispatcher ?? _window?.Dispatcher ?? _placementTimer?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            dispatcher.Invoke(DisposeUiResourcesCore);
            return;
        }

        DisposeUiResourcesCore();
    }

    private void DisposeUiResourcesCore()
    {
        _placementTimer?.Stop();
        if (_placementTimer is not null)
        {
            _placementTimer.Tick -= OnPlacementTimerTick;
            _placementTimer = null;
        }

        if (_window is not null)
        {
            _window.PositionCommitted -= OnPositionCommitted;
            _window.Closed -= OnWindowClosed;
            _window.Close();
            _window = null;
            _windowPetLoaded = false;
        }
    }
}
