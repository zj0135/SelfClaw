using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace SelfClaw.Desktop.Services;

public sealed class SystemTrayService : IDisposable
{
    private static readonly Uri IconUri = new("pack://application:,,,/Assets/icon/icon.ico", UriKind.Absolute);

    private readonly ILogger<SystemTrayService> _logger;
    private readonly Forms.NotifyIcon? _notifyIcon;
    private readonly Forms.ContextMenuStrip? _contextMenu;
    private readonly Forms.ToolStripMenuItem? _openMenuItem;
    private readonly Forms.ToolStripMenuItem? _exitMenuItem;
    private Window? _mainWindow;

    public SystemTrayService(ILogger<SystemTrayService> logger)
    {
        _logger = logger;

        try
        {
            _openMenuItem = new Forms.ToolStripMenuItem("Open SelfClaw");
            _exitMenuItem = new Forms.ToolStripMenuItem("Exit");
            _openMenuItem.Click += OnOpenMenuItemClick;
            _exitMenuItem.Click += OnExitMenuItemClick;

            _contextMenu = new Forms.ContextMenuStrip();
            _contextMenu.Items.AddRange([_openMenuItem, new Forms.ToolStripSeparator(), _exitMenuItem]);

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

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        ActivateMainWindow();
    }

    private void OnOpenMenuItemClick(object? sender, EventArgs e)
    {
        ActivateMainWindow();
    }

    private void OnExitMenuItemClick(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current is null)
        {
            return;
        }

        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()));
    }

    public void ActivateMainWindow()
    {
        var mainWindow = _mainWindow;
        if (mainWindow is null)
        {
            return;
        }

        if (!mainWindow.Dispatcher.CheckAccess())
        {
            _ = mainWindow.Dispatcher.BeginInvoke(new Action(ActivateMainWindow));
            return;
        }

        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Activate();
        mainWindow.Topmost = true;
        mainWindow.Topmost = false;
        mainWindow.Focus();
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

        if (_exitMenuItem is not null)
        {
            _exitMenuItem.Click -= OnExitMenuItemClick;
        }

        _contextMenu?.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);
}
