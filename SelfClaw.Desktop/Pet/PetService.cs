using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Services;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace SelfClaw.Desktop.Pet;

/// <summary>
/// 桌宠生命周期与持久化的单例服务:创建/显示/隐藏浮窗,记住并恢复摆放位置。
/// 位置以「距所在屏幕工作区左上角的偏移 + 屏幕设备名」持久化,恢复时 clamp 进当前工作区,
/// 适配多显示器与分辨率变化(详见 docs/pet-system-design.md §6.3 / §8)。
/// </summary>
public sealed class PetService : IDisposable
{
    private const string SettingsNodeName = "pet";

    /// <summary>拖拽落点写盘的防抖延时:避免拖动结束瞬间的高频写盘。</summary>
    private static readonly TimeSpan PersistDebounce = TimeSpan.FromMilliseconds(400);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly ILogger<PetService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PetSettings _settings = new();
    private bool _loaded;
    private PetWindow? _window;
    private bool _windowPetLoaded;
    private DispatcherTimer? _persistTimer;
    private Point _pendingPosition;
    private bool _disposed;

    public PetService(
        DesktopSettingsJsonStore settingsStore,
        ILogger<PetService> logger,
        ILoggerFactory loggerFactory)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>宠物当前是否可见。</summary>
    public bool IsVisible
    {
        get
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                return dispatcher.Invoke(() => _window is { IsVisible: true });
            }

            return _window is { IsVisible: true };
        }
    }

    public Task<PetSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    public async Task<PetSettings> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled)
        {
            await ShowAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await HideAsync(cancellationToken).ConfigureAwait(false);
        }

        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PetSettings> SelectBuiltInPetAsync(string? petId, CancellationToken cancellationToken = default)
    {
        if (!PetAssetPaths.IsSafeBuiltInPetId(petId))
        {
            throw new ArgumentException("Pet id is invalid.", nameof(petId));
        }

        var normalizedPetId = petId!.Trim();
        var packageDirectory = PetAssetPaths.GetBuiltInPackageDirectory(normalizedPetId);
        if (!Directory.Exists(packageDirectory))
        {
            throw new FileNotFoundException("Built-in pet package was not found.", packageDirectory);
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);

        var next = _settings with
        {
            SpriteSheetPath = normalizedPetId,
            Grid = null,
        };

        await UpdateSettingsAsync(next, cancellationToken).ConfigureAwait(false);

        await InvokeOnUiThreadAsync(
            () =>
            {
                if (_window is null)
                {
                    return;
                }

                _window.LoadPet(_settings);
                _windowPetLoaded = true;
            },
            cancellationToken).ConfigureAwait(false);

        return _settings;
    }

    /// <summary>
    /// 若配置为开启则显示宠物。应用启动时调用一次。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings.Enabled)
        {
            await ShowAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>显示宠物(必要时创建窗口),并持久化开关状态。</summary>
    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);

        await InvokeOnUiThreadAsync(
            () =>
            {
                EnsureWindow();
                if (!_windowPetLoaded)
                {
                    _window!.LoadPet(_settings);
                    _windowPetLoaded = true;
                }

                _window!.Show();
                RestoreWindowPosition();
            },
            cancellationToken).ConfigureAwait(false);

        if (!_settings.Enabled)
        {
            await UpdateSettingsAsync(_settings with { Enabled = true }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>隐藏宠物(保留实例与状态),并持久化开关状态。</summary>
    public async Task HideAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);

        await InvokeOnUiThreadAsync(() => _window?.Hide(), cancellationToken).ConfigureAwait(false);

        if (_settings.Enabled)
        {
            await UpdateSettingsAsync(_settings with { Enabled = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>在显示/隐藏之间切换。</summary>
    public async Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (await GetIsVisibleAsync(cancellationToken).ConfigureAwait(false))
        {
            await HideAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ShowAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<bool> GetIsVisibleAsync(CancellationToken cancellationToken)
    {
        return InvokeOnUiThreadAsync(() => _window is { IsVisible: true }, cancellationToken);
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

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _window = new PetWindow(new PetViewModel(_loggerFactory.CreateLogger<PetViewModel>()));
        _window.PositionCommitted += OnPositionCommitted;
        _window.Closed += OnWindowClosed;
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

    /// <summary>
    /// 把持久化的「工作区相对偏移 + 屏幕设备名」还原为窗口绝对坐标,并 clamp 进当前工作区。
    /// 若从未摆放过,落到主屏工作区右下角的默认位置。
    /// </summary>
    private void RestoreWindowPosition()
    {
        if (_window is null)
        {
            return;
        }

        var screen = ResolveScreen(_settings.ScreenDeviceName);
        var workArea = screen.WorkingArea; // 物理像素
        var (waLeft, waTop, waWidth, waHeight) = PhysicalWorkAreaToDip(workArea);

        var windowWidth = _window.Width;
        var windowHeight = _window.Height;

        double left;
        double top;

        if (_settings.OffsetX is double offsetX && _settings.OffsetY is double offsetY)
        {
            left = waLeft + offsetX;
            top = waTop + offsetY;
        }
        else
        {
            // 默认位置:工作区右下角,留 24 DIP 边距。
            left = waLeft + waWidth - windowWidth - 24d;
            top = waTop + waHeight - windowHeight - 24d;
        }

        // clamp 进工作区,避免屏幕数量/分辨率变化后落到不可见区域。
        left = Math.Clamp(left, waLeft, Math.Max(waLeft, waLeft + waWidth - windowWidth));
        top = Math.Clamp(top, waTop, Math.Max(waTop, waTop + waHeight - windowHeight));

        _window.Left = left;
        _window.Top = top;
    }

    private void OnPositionCommitted(object? sender, Point position)
    {
        // 防抖:拖拽落点稍后写盘。
        _pendingPosition = position;
        _persistTimer ??= new DispatcherTimer { Interval = PersistDebounce };
        _persistTimer.Tick -= OnPersistTimerTick;
        _persistTimer.Tick += OnPersistTimerTick;
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private async void OnPersistTimerTick(object? sender, EventArgs e)
    {
        _persistTimer?.Stop();

        if (_window is null)
        {
            return;
        }

        var screen = ResolveScreenForWindow(_window);
        var workArea = screen.WorkingArea;
        var (waLeft, waTop, _, _) = PhysicalWorkAreaToDip(workArea);

        var next = _settings with
        {
            OffsetX = _pendingPosition.X - waLeft,
            OffsetY = _pendingPosition.Y - waTop,
            ScreenDeviceName = screen.DeviceName,
        };

        try
        {
            await UpdateSettingsAsync(next).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist pet position.");
        }
    }

    private async Task<PetSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                _settings = await _settingsStore
                    .ReadNodeAsync<PetSettings>(SettingsNodeName, JsonOptions, cancellationToken)
                    .ConfigureAwait(false)
                    ?? new PetSettings();
                _loaded = true;
            }

            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateSettingsAsync(PetSettings next, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = next;
            _loaded = true;
            await _settingsStore.WriteNodeAsync(SettingsNodeName, _settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static WinFormsScreen ResolveScreen(string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = Array.Find(
                WinFormsScreen.AllScreens,
                s => string.Equals(s.DeviceName, deviceName, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return WinFormsScreen.PrimaryScreen ?? WinFormsScreen.AllScreens[0];
    }

    private static WinFormsScreen ResolveScreenForWindow(Window window)
    {
        // 用窗口中心点落在哪个屏幕来判定所属屏幕。
        var source = PresentationSource.FromVisual(window);
        var toDevice = source?.CompositionTarget?.TransformToDevice;

        var centerDipX = window.Left + (window.Width / 2d);
        var centerDipY = window.Top + (window.Height / 2d);

        var centerPhysical = toDevice is { } transform
            ? transform.Transform(new Point(centerDipX, centerDipY))
            : new Point(centerDipX, centerDipY);

        return WinFormsScreen.FromPoint(
            new System.Drawing.Point((int)centerPhysical.X, (int)centerPhysical.Y));
    }

    /// <summary>把物理像素工作区矩形转成 DIP,供窗口 Left/Top/尺寸(均为 DIP)使用。</summary>
    private (double Left, double Top, double Width, double Height) PhysicalWorkAreaToDip(System.Drawing.Rectangle workArea)
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

    /// <summary>应用退出时显式关闭浮窗(它不是 MainWindow,不会自动随主窗口关闭)。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeUiResources();
        _gate.Dispose();
    }

    private void DisposeUiResources()
    {
        var dispatcher = Application.Current?.Dispatcher ?? _window?.Dispatcher ?? _persistTimer?.Dispatcher;
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
        _persistTimer?.Stop();
        if (_persistTimer is not null)
        {
            _persistTimer.Tick -= OnPersistTimerTick;
            _persistTimer = null;
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
