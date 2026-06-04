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
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.ViewModels;

namespace SelfClaw.Desktop;

public partial class MainWindow : Window
{
    private const double ExpandedRightPanelWidth = 320d;
    private static readonly Duration DrawerAnimationDuration = TimeSpan.FromMilliseconds(180);
    private const string AssetsHostName = "appassets.selfclaw.local";
    private const int DefaultTerminalColumns = 120;
    private const int DefaultTerminalRows = 24;
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
    private bool _terminalReady;
    private bool _isTerminalFocused;
    private bool _isRightPanelOpen;
    private string? _activeRightPanelTool;
    private DispatcherTimer? _rightPanelAnimationTimer;
    private ConPtyTerminalSession? _terminalSession;
    private string _terminalWorkingDirectory = ResolveDefaultTerminalWorkingDirectory();
    private int _terminalColumns = DefaultTerminalColumns;
    private int _terminalRows = DefaultTerminalRows;

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
        StopTerminalSession();

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
        PostTerminalState();
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
            if (_isTerminalFocused && _terminalSession is not null)
            {
                _terminalSession.WriteInput("\x1b");
                FocusTranscriptView();
                e.Handled = true;
                return;
            }

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

    private async void OnSidebarNewConversationRequested(object? sender, EventArgs e)
    {
        SetSystemSettingsOpen(false);
        await _viewModel.CreateStandaloneConversationFromUiAsync();
    }

    private void OnSidebarProjectsToggleRequested(object? sender, EventArgs e)
    {
        _viewModel.ToggleSidebarProjects();
    }

    private async void OnSidebarStandaloneConversationRequested(object? sender, EventArgs e)
    {
        SetSystemSettingsOpen(false);
        await _viewModel.CreateStandaloneConversationFromUiAsync();
    }

    private void OnSidebarStandaloneConversationsToggleRequested(object? sender, EventArgs e)
    {
        _viewModel.ToggleSidebarStandaloneConversations();
    }

    private async void OnSidebarWorkspaceRootSelected(object? sender, Guid workspaceRootId)
    {
        SetSystemSettingsOpen(false);
        await _viewModel.ToggleSidebarWorkspaceRootAsync(workspaceRootId);
    }

    private async void OnSidebarProjectConversationRequested(object? sender, Guid workspaceRootId)
    {
        SetSystemSettingsOpen(false);
        await _viewModel.CreateProjectConversationFromUiAsync(workspaceRootId);
    }

    private async void OnSidebarConversationSelected(object? sender, Guid conversationId)
    {
        SetSystemSettingsOpen(false);
        await _viewModel.SelectConversationAsync(conversationId);
    }

    private async void OnSidebarConversationDeleteRequested(object? sender, Guid conversationId)
    {
        SetSystemSettingsOpen(false);
        await _viewModel.DeleteConversationAsync(conversationId);
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
        if (!isOpen)
        {
            _isTerminalFocused = false;
        }

        if (isOpen)
        {
            var resolvedWorkingDirectory = ResolveTerminalWorkingDirectory();
            if (!PathsEqual(_terminalWorkingDirectory, resolvedWorkingDirectory))
            {
                _terminalWorkingDirectory = resolvedWorkingDirectory;
                StopTerminalSession();
            }

            EnsureTerminalSession();
            FocusTranscriptView();
        }

        PostTerminalState();
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

    private string ResolveTerminalWorkingDirectory()
    {
        var workspaceRootPath = _viewModel.SelectedWorkspaceRootPath;
        if (!string.IsNullOrWhiteSpace(workspaceRootPath) && Directory.Exists(workspaceRootPath))
        {
            return workspaceRootPath;
        }

        return ResolveDefaultTerminalWorkingDirectory();
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
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedWorkspaceRootPath) && _isTerminalDrawerOpen)
        {
            ResetTerminalWorkingDirectoryIfNeeded();
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
                case "terminal-ready":
                    _terminalReady = true;
                    ApplyTerminalResize(document.RootElement);
                    PostTerminalState();
                    if (_isTerminalDrawerOpen)
                    {
                        EnsureTerminalSession();
                    }
                    break;
                case "terminal-input":
                    if (_terminalSession is not null && document.RootElement.TryGetProperty("data", out var dataElement))
                    {
                        _terminalSession.WriteInput(dataElement.GetString() ?? string.Empty);
                    }
                    break;
                case "terminal-resize":
                    ApplyTerminalResize(document.RootElement);
                    break;
                case "terminal-focus-change":
                    _isTerminalFocused = document.RootElement.TryGetProperty("isFocused", out var isFocusedElement) &&
                                         isFocusedElement.GetBoolean() &&
                                         _isTerminalDrawerOpen;
                    break;
                case "terminal-close":
                    SetTerminalDrawerOpen(false);
                    break;
                case "terminal-restart":
                    RestartTerminalSession();
                    break;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void EnsureTerminalSession()
    {
        if (!_terminalReady || _terminalSession is not null)
        {
            return;
        }

        try
        {
            _terminalSession = new ConPtyTerminalSession(
                ResolvePowerShellExecutable(),
                _terminalWorkingDirectory,
                _terminalColumns,
                _terminalRows);
            _terminalSession.OutputReceived += OnTerminalOutputReceived;
            _terminalSession.Exited += OnTerminalExited;
            _terminalSession.Start();
            PostTerminalState();
            PostTerminalFocus();
        }
        catch (Exception exception)
        {
            PostTerminalOutput($"\r\nFailed to start terminal: {exception.Message}\r\n");
        }
    }

    private void RestartTerminalSession()
    {
        StopTerminalSession();
        PostTerminalMessage(new { type = "terminal-clear" });
        if (_isTerminalDrawerOpen)
        {
            EnsureTerminalSession();
        }
    }

    private void StopTerminalSession()
    {
        var session = _terminalSession;
        _terminalSession = null;
        if (session is null)
        {
            return;
        }

        session.OutputReceived -= OnTerminalOutputReceived;
        session.Exited -= OnTerminalExited;
        session.Dispose();
        _isTerminalFocused = false;
        PostTerminalState();
    }

    private void OnTerminalOutputReceived(object? sender, string data)
    {
        _ = Dispatcher.BeginInvoke(new Action(() => PostTerminalOutput(data)), DispatcherPriority.Background);
    }

    private void OnTerminalExited(object? sender, int? exitCode)
    {
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                var exitedSession = sender as ConPtyTerminalSession;
                if (ReferenceEquals(sender, _terminalSession))
                {
                    _terminalSession = null;
                }

                if (exitedSession is not null)
                {
                    exitedSession.OutputReceived -= OnTerminalOutputReceived;
                    exitedSession.Exited -= OnTerminalExited;
                    exitedSession.Dispose();
                }

                PostTerminalOutput(exitCode is int code
                    ? $"\r\n[terminal exited with code {code}]\r\n"
                    : "\r\n[terminal exited]\r\n");
                PostTerminalState();
            }),
            DispatcherPriority.Background);
    }

    private void ApplyTerminalResize(JsonElement root)
    {
        if (!root.TryGetProperty("cols", out var colsElement) ||
            !root.TryGetProperty("rows", out var rowsElement))
        {
            return;
        }

        var cols = Math.Max(1, colsElement.GetInt32());
        var rows = Math.Max(1, rowsElement.GetInt32());
        _terminalColumns = cols;
        _terminalRows = rows;
        _terminalSession?.Resize(cols, rows);
    }

    private void PostTerminalOutput(string data)
        => PostTerminalMessage(new
        {
            type = "terminal-output",
            data
        });

    private void PostTerminalState()
        => PostTerminalMessage(new
        {
            type = "terminal-state",
            isOpen = _isTerminalDrawerOpen,
            isRunning = _terminalSession is not null,
            cwd = _terminalWorkingDirectory
        });

    private void PostTerminalFocus()
        => PostTerminalMessage(new { type = "terminal-focus" });

    private void PostTerminalMessage(object payload)
    {
        if (!_webViewReady || TranscriptView.CoreWebView2 is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        TranscriptView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void FocusTranscriptView()
    {
        if (TranscriptView.Visibility != Visibility.Visible)
        {
            return;
        }

        TranscriptView.Focus();
        Keyboard.Focus(TranscriptView);
        PostTerminalFocus();
    }

    private static string ResolvePowerShellExecutable()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    private static string ResolveDefaultTerminalWorkingDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath) && Directory.Exists(desktopPath))
        {
            return desktopPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile) ? AppContext.BaseDirectory : userProfile;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private void ResetTerminalWorkingDirectoryIfNeeded()
    {
        var resolvedWorkingDirectory = ResolveTerminalWorkingDirectory();
        if (PathsEqual(_terminalWorkingDirectory, resolvedWorkingDirectory))
        {
            return;
        }

        _terminalWorkingDirectory = resolvedWorkingDirectory;
        StopTerminalSession();
        if (_isTerminalDrawerOpen)
        {
            EnsureTerminalSession();
        }

        PostTerminalState();
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

