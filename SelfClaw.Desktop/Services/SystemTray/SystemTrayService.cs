using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Pet;
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
    private readonly Forms.NotifyIcon? _notifyIcon;
    private readonly Forms.ContextMenuStrip? _contextMenu;
    private readonly Forms.ToolStripMenuItem? _openMenuItem;
    private readonly Forms.ToolStripMenuItem? _petMenuItem;
    private readonly Forms.ToolStripMenuItem? _exitMenuItem;
    private Window? _mainWindow;
    private int _disposeStarted;

    public SystemTrayService(ILogger<SystemTrayService> logger, PetHost petHost, DesktopConversationActivationService activation)
    {
        _logger = logger;
        _petHost = petHost;
        _activation = activation;

        try
        {
            _openMenuItem = new Forms.ToolStripMenuItem("Open SelfClaw");
            _petMenuItem = new Forms.ToolStripMenuItem("Show Pet") { CheckOnClick = true };
            _exitMenuItem = new Forms.ToolStripMenuItem("Exit");
            _openMenuItem.Click += OnOpenMenuItemClick;
            _petMenuItem.Click += OnPetMenuItemClick;
            _exitMenuItem.Click += OnExitMenuItemClick;

            _contextMenu = new Forms.ContextMenuStrip();
            _contextMenu.Items.AddRange([_openMenuItem, _petMenuItem, new Forms.ToolStripSeparator(), _exitMenuItem]);
            _contextMenu.Opening += OnContextMenuOpening;

            _notifyIcon = new Forms.NotifyIcon
            {
                Text = "SelfClaw",
                Icon = LoadTrayIcon(),
                Visible = true,
                ContextMenuStrip = _contextMenu
            };
            _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
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

    private async void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        await _activation.ActivateAsync();
    }

    private async void OnOpenMenuItemClick(object? sender, EventArgs e)
    {
        await _activation.ActivateAsync();
    }

    private async void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        await RefreshPetMenuItemAsync();
    }

    private async void OnPetMenuItemClick(object? sender, EventArgs e)
    {
        try
        {
            var state = await _petHost.ExecuteAsync(new PetHostCommand(PetHostCommandKind.Toggle));
            SyncPetMenuItem(state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to toggle the desktop pet.");
            await RefreshPetMenuItemAsync();
        }
    }

    private async Task RefreshPetMenuItemAsync()
    {
        try
        {
            SyncPetMenuItem(await _petHost.GetStateAsync());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read the desktop pet state.");
        }
    }

    private void SyncPetMenuItem(PetHostState state)
    {
        if (_petMenuItem is null)
        {
            return;
        }

        _petMenuItem.Checked = state.IsVisible;
        _petMenuItem.Text = state.IsVisible ? "Hide Pet" : "Show Pet";
    }

    private void OnExitMenuItemClick(object? sender, EventArgs e)
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        if (_notifyIcon is not null)
        {
            _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
        }

        if (_openMenuItem is not null)
        {
            _openMenuItem.Click -= OnOpenMenuItemClick;
        }

        if (_petMenuItem is not null)
        {
            _petMenuItem.Click -= OnPetMenuItemClick;
        }

        if (_exitMenuItem is not null)
        {
            _exitMenuItem.Click -= OnExitMenuItemClick;
        }

        if (_contextMenu is not null)
        {
            _contextMenu.Opening -= OnContextMenuOpening;
            _contextMenu.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);
}
