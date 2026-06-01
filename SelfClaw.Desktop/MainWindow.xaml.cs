using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.ViewModels;

namespace SelfClaw.Desktop;

public partial class MainWindow : Window
{
    private const double ExpandedRightPanelWidth = 320d;
    private const double ExpandedTerminalDrawerHeight = 286d;
    private static readonly Duration DrawerAnimationDuration = TimeSpan.FromMilliseconds(180);
    private const string AssetsHostName = "appassets.selfclaw.local";
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;
    private const double StartupWorkAreaMargin = 48d;

    private readonly MainWindowViewModel _viewModel;
    private TranscriptRenderState _pendingTranscript = new(
        Items: [],
        AutoScroll: false,
        Conversations: [],
        SelectedConversationId: null,
        Theme: "light",
        IsBusy: false);
    private bool _webViewReady;
    private bool _isTerminalDrawerOpen;
    private bool _isRightPanelOpen;
    private string? _activeRightPanelTool;
    private DispatcherTimer? _terminalDrawerAnimationTimer;
    private DispatcherTimer? _rightPanelAnimationTimer;

    public MainWindow(
        MainWindowViewModel viewModel,
        DesktopNotificationService desktopNotificationService)
    {
        InitializeComponent();
        ApplyAdaptiveStartupSize();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoadedAsync;
        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += HandlePreviewKeyDown;
        Closed += OnClosed;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        TranscriptView.NavigationCompleted += OnTranscriptNavigationCompleted;
        desktopNotificationService.RegisterMainWindow(this);
    }

    private void ApplyAdaptiveStartupSize()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(1d, workArea.Width - StartupWorkAreaMargin);
        var availableHeight = Math.Max(1d, workArea.Height - StartupWorkAreaMargin);

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Math.Max(Width, MinWidth), availableWidth);
        Height = Math.Min(Math.Max(Height, MinHeight), availableHeight);
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        ApplyThemeMode();
        await EnsureTranscriptHostAsync();
        await _viewModel.InitializeAsync();
        ApplyThemeMode();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.TranscriptChanged -= OnTranscriptChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        TranscriptView.NavigationCompleted -= OnTranscriptNavigationCompleted;

        if (TranscriptView.CoreWebView2 is not null)
        {
            TranscriptView.CoreWebView2.WebMessageReceived -= OnTranscriptWebMessageReceived;
        }

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.RemoveHook(WndProc);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowBackdropHelper.TryApplySystemBackdrop(this);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private async Task EnsureTranscriptHostAsync()
    {
        try
        {
            await TranscriptView.EnsureCoreWebView2Async();
            TranscriptView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            TranscriptView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            TranscriptView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            TranscriptView.CoreWebView2.WebMessageReceived += OnTranscriptWebMessageReceived;

            var assetsRootPath = Path.Combine(AppContext.BaseDirectory, "Assets");
            var vueTranscriptPath = Path.Combine(assetsRootPath, "TranscriptVue", "index.html");

            if (!File.Exists(vueTranscriptPath))
            {
                throw new FileNotFoundException("Unable to locate the Vue transcript host page.", vueTranscriptPath);
            }

            TranscriptView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AssetsHostName,
                assetsRootPath,
                CoreWebView2HostResourceAccessKind.Allow);

            TranscriptView.Source = new Uri($"https://{AssetsHostName}/TranscriptVue/index.html");
        }
        catch
        {
            TranscriptView.Visibility = Visibility.Collapsed;
            WebViewFallback.Visibility = Visibility.Visible;
        }
    }

    private void OnTranscriptNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            return;
        }

        _webViewReady = true;
        PostTranscript(_pendingTranscript);
    }

    private void OnTranscriptChanged(object? sender, TranscriptRenderState state)
    {
        _pendingTranscript = state;
        PostTranscript(state);
    }

    private void PostTranscript(TranscriptRenderState state)
    {
        if (!_webViewReady || TranscriptView.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "replaceState",
            autoScroll = state.AutoScroll,
            items = state.Items,
            theme = state.Theme,
            conversations = state.Conversations,
            selectedConversationId = state.SelectedConversationId,
            isBusy = state.IsBusy
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        TranscriptView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private void HandlePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.StopGeneration();
            e.Handled = true;
        }
    }

    private void OnTitleBarDragRegionMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Ignore transient drag failures while the shell is processing input.
        }
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnToggleMaximizeButtonClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnTerminalToolButtonClick(object sender, RoutedEventArgs e)
    {
        SetSystemSettingsOpen(false);
        SetTerminalDrawerOpen(!_isTerminalDrawerOpen);
    }

    private void OnTerminalPanelCloseRequested(object? sender, EventArgs e)
    {
        SetTerminalDrawerOpen(false);
    }

    private void OnFileManagerToolButtonClick(object sender, RoutedEventArgs e)
    {
        SetSystemSettingsOpen(false);
        ToggleRightPanelTool("files");
        SetTerminalDrawerOpen(false);
    }

    private void OnBrowserToolButtonClick(object sender, RoutedEventArgs e)
    {
        SetSystemSettingsOpen(false);
        ToggleRightPanelTool("browser");
        SetTerminalDrawerOpen(false);
    }

    private void OnSystemSettingsRequested(object? sender, EventArgs e)
    {
        SetSystemSettingsOpen(true);
        SetTerminalDrawerOpen(false);
        SetRightPanelOpen(false);
    }

    private void SetSystemSettingsOpen(bool isOpen)
    {
        SystemSettingsPanelHost.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

        if (WebViewFallback.Visibility == Visibility.Visible)
        {
            return;
        }

        TranscriptView.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetTerminalDrawerOpen(bool isOpen)
    {
        _isTerminalDrawerOpen = isOpen;
        if (isOpen)
        {
            TerminalDrawerHost.Visibility = Visibility.Visible;
        }

        AnimateGridLength(
            ref _terminalDrawerAnimationTimer,
            TerminalDrawerRow.Height.Value,
            isOpen ? ExpandedTerminalDrawerHeight : 0,
            value => TerminalDrawerRow.Height = new GridLength(value),
            isOpen ? null : () => TerminalDrawerHost.Visibility = Visibility.Collapsed);
    }

    private void ToggleRightPanelTool(string toolId)
    {
        var shouldClose = _isRightPanelOpen && string.Equals(_activeRightPanelTool, toolId, StringComparison.Ordinal);
        SetRightPanelOpen(!shouldClose, shouldClose ? null : toolId);
    }

    private void SetRightPanelOpen(bool isOpen, string? activeToolId = null)
    {
        _isRightPanelOpen = isOpen;
        _activeRightPanelTool = isOpen ? activeToolId : null;
        if (isOpen)
        {
            RightPanelHost.Visibility = Visibility.Visible;
        }

        AnimateGridLength(
            ref _rightPanelAnimationTimer,
            RightPanelColumn.Width.Value,
            isOpen ? ExpandedRightPanelWidth : 0,
            value => RightPanelColumn.Width = new GridLength(value),
            isOpen ? null : () => RightPanelHost.Visibility = Visibility.Collapsed);
    }

    private void AnimateGridLength(
        ref DispatcherTimer? timer,
        double from,
        double to,
        Action<double> applyValue,
        Action? completed)
    {
        timer?.Stop();

        if (Math.Abs(from - to) < 0.5d)
        {
            applyValue(to);
            completed?.Invoke();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        animationTimer.Tick += (_, _) =>
        {
            var rawProgress = Math.Min(1d, stopwatch.Elapsed.TotalMilliseconds / DrawerAnimationDuration.TimeSpan.TotalMilliseconds);
            var easedProgress = 1d - Math.Pow(1d - rawProgress, 2d);
            applyValue(from + ((to - from) * easedProgress));

            if (rawProgress < 1d)
            {
                return;
            }

            animationTimer.Stop();
            applyValue(to);
            completed?.Invoke();
        };

        timer = animationTimer;
        animationTimer.Start();
    }

    private void ToggleWindowState()
    {
        if (ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip))
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActiveThemeMode))
        {
            ApplyThemeMode();
        }
    }

    private void ApplyThemeMode()
    {
        ThemeMode = _viewModel.ActiveThemeMode;
        WindowBackdropHelper.TryApplyCaptionTheme(this, false);
    }

    private async void OnTranscriptWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "open-link":
                {
                    var href = document.RootElement.GetProperty("href").GetString();
                    if (!string.IsNullOrWhiteSpace(href))
                    {
                        Process.Start(new ProcessStartInfo(href) { UseShellExecute = true });
                    }
                    break;
                }
                case "send-prompt":
                {
                    var prompt = document.RootElement.TryGetProperty("prompt", out var promptElement)
                        ? promptElement.GetString() ?? string.Empty
                        : string.Empty;
                    await _viewModel.SubmitPromptAsync(prompt);
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyMaximizedBounds(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo();
        monitorInfo.CbSize = Marshal.SizeOf<MonitorInfo>();
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var workArea = monitorInfo.RcWork;
        var monitorArea = monitorInfo.RcMonitor;
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        minMaxInfo.PtMaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
        minMaxInfo.PtMaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
        minMaxInfo.PtMaxSize.X = Math.Abs(workArea.Right - workArea.Left);
        minMaxInfo.PtMaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
        minMaxInfo.PtMaxTrackSize = minMaxInfo.PtMaxSize;

        Marshal.StructureToPtr(minMaxInfo, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point PtReserved;
        public Point PtMaxSize;
        public Point PtMaxPosition;
        public Point PtMinTrackSize;
        public Point PtMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public int DwFlags;
    }
}

