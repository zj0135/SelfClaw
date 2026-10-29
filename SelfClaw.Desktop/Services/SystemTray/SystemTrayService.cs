using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services.Appearance;
using SelfClaw.Desktop.Services.Windowing;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace SelfClaw.Desktop.Services.SystemTray;

internal sealed class SystemTrayService : IDisposable
{
    private static readonly Uri IconUri = new("pack://application:,,,/Assets/icon/icon.ico", UriKind.Absolute);

    private readonly ILogger<SystemTrayService> _logger;
    private readonly PetHost _petHost;
    private readonly DesktopConversationActivationService _activation;
    private readonly AppearanceSettingsService _appearanceSettingsService;
    private readonly Forms.NotifyIcon? _notifyIcon;
    private TrayMenuWindow? _trayMenuWindow;
    private Window? _mainWindow;
    private int _disposeStarted;

    public SystemTrayService(
        ILogger<SystemTrayService> logger,
        PetHost petHost,
        DesktopConversationActivationService activation,
        AppearanceSettingsService appearanceSettingsService)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(petHost);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(appearanceSettingsService);

        _logger = logger;
        _petHost = petHost;
        _activation = activation;
        _appearanceSettingsService = appearanceSettingsService;

        try
        {
            _notifyIcon = new Forms.NotifyIcon
            {
                Text = "SelfClaw",
                Icon = LoadTrayIcon(),
                Visible = true
            };
            _notifyIcon.MouseUp += OnNotifyIconMouseUp;
            _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;

            InitializeTrayMenuWindow();
            _petHost.Changed += OnPetHostChanged;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to initialize the system tray icon.");
        }
    }

    public void RegisterMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        _petHost.Changed -= OnPetHostChanged;

        if (_notifyIcon is not null)
        {
            _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
            _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
        }

        if (_trayMenuWindow is not null)
        {
            _trayMenuWindow.TogglePetRequested -= OnTogglePetAsync;
            _trayMenuWindow.OpenMainWindowRequested -= OnOpenMainWindowAsync;
            _trayMenuWindow.ExitRequested -= OnExitRequested;

            if (_trayMenuWindow.Dispatcher.CheckAccess())
            {
                _trayMenuWindow.Close();
            }
            else
            {
                _trayMenuWindow.Dispatcher.Invoke(() => _trayMenuWindow.Close());
            }

            _trayMenuWindow = null;
        }
    }

    private void InitializeTrayMenuWindow()
    {
        void Create()
        {
            _trayMenuWindow = new TrayMenuWindow();
            _trayMenuWindow.TogglePetRequested += OnTogglePetAsync;
            _trayMenuWindow.OpenMainWindowRequested += OnOpenMainWindowAsync;
            _trayMenuWindow.ExitRequested += OnExitRequested;
        }

        if (System.Windows.Application.Current is { Dispatcher: { } dispatcher } && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Create);
            return;
        }

        Create();
    }

    private async void OnNotifyIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Right || _trayMenuWindow is null)
        {
            return;
        }

        if (_trayMenuWindow.IsVisible)
        {
            _trayMenuWindow.Hide();
            return;
        }

        try
        {
            var petState = await _petHost.GetStateAsync();
            _trayMenuWindow.UpdatePetState(petState.IsVisible);
            _trayMenuWindow.ApplyTheme(_appearanceSettingsService.CachedIsDark);
            _trayMenuWindow.ShowAtCursor();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to show custom tray menu.");
        }
    }

    private async void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        _trayMenuWindow?.Hide();
        await _activation.ActivateAsync();
    }

    private async Task OnTogglePetAsync()
    {
        try
        {
            var state = await _petHost.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Toggle));
            _trayMenuWindow?.UpdatePetState(state.IsVisible);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to toggle desktop pet.");
            await RefreshPetStateAsync();
        }
    }

    private async Task OnOpenMainWindowAsync()
    {
        await _activation.ActivateAsync();
    }

    private void OnExitRequested()
    {
        if (System.Windows.Application.Current is null)
        {
            return;
        }

        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            _mainWindow?.Close();
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => _mainWindow?.Close()));
    }

    private async void OnPetHostChanged()
    {
        await RefreshPetStateAsync();
    }

    private async Task RefreshPetStateAsync()
    {
        if (_trayMenuWindow is null)
        {
            return;
        }

        try
        {
            var state = await _petHost.GetStateAsync();
            if (_trayMenuWindow.Dispatcher.CheckAccess())
            {
                _trayMenuWindow.UpdatePetState(state.IsVisible);
            }
            else
            {
                _trayMenuWindow.Dispatcher.Invoke(() => _trayMenuWindow.UpdatePetState(state.IsVisible));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read desktop pet state.");
        }
    }

    private static DrawingIcon LoadTrayIcon()
    {
        using var bitmap = LoadIconBitmap();
        var handle = bitmap.GetHicon();

        try
        {
            using var nativeIcon = DrawingIcon.FromHandle(handle);
            return (DrawingIcon)nativeIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static DrawingBitmap LoadIconBitmap()
    {
        var streamResourceInfo = System.Windows.Application.GetResourceStream(IconUri);
        if (streamResourceInfo?.Stream is null)
        {
            throw new FileNotFoundException("Unable to locate the embedded tray icon resource.", IconUri.ToString());
        }

        using var resourceStream = streamResourceInfo.Stream;
        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.DecodePixelWidth = 32;
        bitmapImage.StreamSource = resourceStream;
        bitmapImage.EndInit();
        bitmapImage.Freeze();

        using var encodedStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
        encoder.Save(encodedStream);
        encodedStream.Position = 0;

        using var sourceBitmap = new DrawingBitmap(encodedStream);
        return new DrawingBitmap(sourceBitmap);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);
}
