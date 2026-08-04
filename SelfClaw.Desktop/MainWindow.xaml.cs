using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop;

public partial class MainWindow : Window
{
    private const double ExpandedRightPanelWidth = 320d;
    private static readonly Duration DrawerAnimationDuration = TimeSpan.FromMilliseconds(180);
    private const string AssetsHostName = "appassets.selfclaw.local";
    private const string AttachmentHostName = "attachments.selfclaw.local";
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcLButtonDown = 0x00A1;
    private const uint MonitorDefaultToNearest = 2;
    private const double StartupWorkAreaMargin = 48d;
    private static readonly IntPtr HtCaption = new(2);

    private readonly MainWindowViewModel _viewModel;
    private readonly StoragePaths _storagePaths;
    private readonly PetActivityPresenter _petActivityPresenter;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly DesktopNotificationService _desktopNotificationService;
    private readonly WebViewHostChannel _webViewHostChannel;
    private readonly WebViewMessageRouter _webViewMessageRouter;
    private readonly TerminalHostController _terminalHostController;
    private bool _isRightPanelOpen;
    private bool _isSystemSettingsOpen;
    private string? _activeRightPanelTool;
    private DispatcherTimer? _rightPanelAnimationTimer;
    private Guid? _currentApprovalId;

    internal MainWindow(
        MainWindowViewModel viewModel,
        DesktopNotificationService desktopNotificationService,
        DesktopToolApprovalHandler toolApprovalHandler,
        PetActivityPresenter petActivityPresenter,
        AgentActivityCoordinator agentActivityCoordinator,
        WebViewHostChannel webViewHostChannel,
        WebViewMessageRouter webViewMessageRouter,
        TerminalHostController terminalHostController,
        StoragePaths storagePaths)
    {
        InitializeComponent();
        ApplyAdaptiveStartupSize();
        _viewModel = viewModel;
        _storagePaths = storagePaths;
        _petActivityPresenter = petActivityPresenter;
        _agentActivityCoordinator = agentActivityCoordinator;
        _webViewHostChannel = webViewHostChannel;
        _webViewMessageRouter = webViewMessageRouter;
        _terminalHostController = terminalHostController;
        _toolApprovalHandler = toolApprovalHandler;
        _desktopNotificationService = desktopNotificationService;
        DataContext = viewModel;
        Loaded += OnLoadedAsync;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += HandlePreviewKeyDown;
        Closed += OnClosed;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        TranscriptView.NavigationCompleted += OnTranscriptNavigationCompleted;
        TranscriptView.NavigationStarting += OnTranscriptNavigationStarting;
        _toolApprovalHandler.ApprovalRequested += OnToolApprovalRequested;
        _toolApprovalHandler.ApprovalExpired += OnToolApprovalExpired;
        _agentActivityCoordinator.SnapshotChanged += OnAgentActivitySnapshotChanged;
        _petActivityPresenter.ConversationActivationRequested += OnPetConversationActivationRequested;
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
        await EnsureTranscriptHostAsync();
        await _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        StateChanged -= OnWindowStateChanged;
        TranscriptView.NavigationCompleted -= OnTranscriptNavigationCompleted;
        TranscriptView.NavigationStarting -= OnTranscriptNavigationStarting;
        _toolApprovalHandler.ApprovalRequested -= OnToolApprovalRequested;
        _toolApprovalHandler.ApprovalExpired -= OnToolApprovalExpired;
        _agentActivityCoordinator.SnapshotChanged -= OnAgentActivitySnapshotChanged;
        _petActivityPresenter.ConversationActivationRequested -= OnPetConversationActivationRequested;
        _toolApprovalHandler.RejectAll();
        _terminalHostController.Dispose();

        if (TranscriptView.CoreWebView2 is not null)
        {
            TranscriptView.CoreWebView2.WebMessageReceived -= OnTranscriptWebMessageReceived;
        }

        _webViewHostChannel.Detach();

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.RemoveHook(WndProc);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowBackdropHelper.TryApplySystemBackdrop(this);
        WindowBackdropHelper.TryApplyCaptionTheme(this, false);

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
            _webViewHostChannel.Attach(TranscriptView.CoreWebView2.PostWebMessageAsJson);

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

            var attachmentsRootPath = Path.Combine(_storagePaths.AppDataDirectory, "attachments");
            Directory.CreateDirectory(attachmentsRootPath);
            TranscriptView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AttachmentHostName,
                attachmentsRootPath,
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

        _webViewHostChannel.MarkReady();
        _terminalHostController.PublishState();
        PostWindowState();
        RepostCurrentApproval();
    }

    private void OnTranscriptNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        => _webViewHostChannel.MarkNotReady();

    // A WebView reload drops the inline approval bar; re-send the request that is still pending so the
    // bar comes back rather than leaving the turn blocked with no visible way to answer.
    private void RepostCurrentApproval()
    {
        var current = _agentActivityCoordinator.CurrentSnapshot.Approval;
        if (current is not null)
        {
            _currentApprovalId = current.ToolExecutionId;
            PostToolApprovalRequest(current);
        }
    }

    private void HandlePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_terminalHostController.TryWriteEscape())
            {
                FocusTranscriptView();
                e.Handled = true;
                return;
            }

            _viewModel.StopSelectedConversation();
        }
    }

    private void OnFallbackCloseButtonClick(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleTerminalTool()
    {
        SetSystemSettingsOpen(false);
        SetTerminalDrawerOpen(!_terminalHostController.IsOpen);
    }

    private void ToggleFileManagerTool()
    {
        SetSystemSettingsOpen(false);
        ToggleRightPanelTool("files");
        SetTerminalDrawerOpen(false);
    }

    private void ToggleBrowserTool()
    {
        SetSystemSettingsOpen(false);
        ToggleRightPanelTool("browser");
        SetTerminalDrawerOpen(false);
    }

    private void SetSystemSettingsOpen(bool isOpen)
    {
        _isSystemSettingsOpen = isOpen;
        PostSettingsState();
    }

    private void PostSettingsState()
        => _webViewHostChannel.PostPush(new
        {
            type = _isSystemSettingsOpen ? "show-settings" : "hide-settings"
        });

    private void OnSettingsClosedFromWebView()
    {
        SetSystemSettingsOpen(false);
    }

    private void SetTerminalDrawerOpen(bool isOpen)
    {
        _terminalHostController.SetOpen(isOpen, _viewModel.SelectedWorkspaceRootPath);
        if (isOpen)
        {
            FocusTranscriptView();
        }
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

    private void StartWindowDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => PostWindowState();

    private void PostWindowState()
        => _webViewHostChannel.PostPush(new
        {
            type = "window-state",
            isMaximized = WindowState == WindowState.Maximized
        });

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedWorkspaceRootPath) && _terminalHostController.IsOpen)
        {
            _terminalHostController.UpdateWorkspaceRoot(_viewModel.SelectedWorkspaceRootPath);
        }
    }

    private async void OnTranscriptWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var command = await _webViewMessageRouter.RouteAsync(
                e.WebMessageAsJson,
                new WindowInteropHelper(this).Handle);
            ApplyWebViewHostCommand(command);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void ApplyWebViewHostCommand(WebViewHostCommand? command)
    {
        if (command is null)
        {
            return;
        }

        switch (command.Kind)
        {
            case WebViewHostCommandKind.OpenLink:
                if (!string.IsNullOrWhiteSpace(command.Value))
                {
                    Process.Start(new ProcessStartInfo(command.Value) { UseShellExecute = true });
                }
                break;
            case WebViewHostCommandKind.StartWindowDrag:
                StartWindowDrag();
                break;
            case WebViewHostCommandKind.MinimizeWindow:
                WindowState = WindowState.Minimized;
                break;
            case WebViewHostCommandKind.ToggleMaximizeWindow:
                ToggleWindowState();
                break;
            case WebViewHostCommandKind.CloseWindow:
                Close();
                break;
            case WebViewHostCommandKind.ToggleTerminal:
                ToggleTerminalTool();
                break;
            case WebViewHostCommandKind.ToggleFiles:
                ToggleFileManagerTool();
                break;
            case WebViewHostCommandKind.ToggleBrowser:
                ToggleBrowserTool();
                break;
            case WebViewHostCommandKind.SettingsClosed:
                OnSettingsClosedFromWebView();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unsupported WebView host command.");
        }
    }

    private void OnToolApprovalRequested(ToolApprovalRequest request)
    {
        var summary = BuildToolApprovalSummary(request);
        _desktopNotificationService.ShowToolApproval(
            request.ToolExecutionId,
            request.ConversationId,
            request.DisplayName,
            summary);

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            _toolApprovalHandler.TryResolve(request.ToolExecutionId, approved: false);
            return;
        }

    }

    private void OnAgentActivitySnapshotChanged(object? sender, AgentActivitySnapshot snapshot)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            SyncCurrentApproval();
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(SyncCurrentApproval), DispatcherPriority.Normal);
    }

    private void SyncCurrentApproval()
    {
        var current = _agentActivityCoordinator.CurrentSnapshot.Approval;
        if (_currentApprovalId == current?.ToolExecutionId)
        {
            return;
        }

        _currentApprovalId = current?.ToolExecutionId;
        if (current is null)
        {
            PostToolApprovalClear();
            return;
        }

        PostToolApprovalRequest(current);
    }

    private void PostToolApprovalRequest(ToolApprovalRequest request)
        => _webViewHostChannel.PostPush(new
        {
            type = "toolApprovalRequest",
            toolExecutionId = request.ToolExecutionId.ToString(),
            toolName = request.ToolName,
            displayName = request.DisplayName,
            description = request.Description,
            argumentsJson = request.ArgumentsJson,
            sourceKind = request.SourceKind,
            sourceId = request.SourceId,
            transportSummary = request.TransportSummary,
            annotationsJson = request.AnnotationsJson
        });

    private void PostToolApprovalClear()
        => _webViewHostChannel.PostPush(new { type = "toolApprovalClear" });

    private void OnToolApprovalExpired(ToolApprovalRequest request)
    {
        // Queue cleanup happens through OnToolApprovalCompleted; only surface the timeout as a toast.
        _desktopNotificationService.ShowToolApprovalExpired(request.DisplayName);
    }

    private void OnPetConversationActivationRequested(object? sender, Guid conversationId)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => ActivateConversationFromPet(conversationId),
                DispatcherPriority.Normal);
            return;
        }

        ActivateConversationFromPet(conversationId);
    }

    private void ActivateConversationFromPet(Guid conversationId)
    {
        try
        {
            _ = _viewModel.SelectConversationAsync(conversationId);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
    }

    private static string BuildToolApprovalSummary(ToolApprovalRequest request)
    {
        const int maxArgumentsLength = 2000;
        var arguments = string.IsNullOrWhiteSpace(request.ArgumentsJson)
            ? "(no arguments)"
            : request.ArgumentsJson.Trim();
        if (arguments.Length > maxArgumentsLength)
        {
            arguments = $"{arguments[..maxArgumentsLength]}…";
        }

        var description = string.IsNullOrWhiteSpace(request.Description)
            ? request.ToolName
            : request.Description.Trim();
        var source = string.IsNullOrWhiteSpace(request.SourceId)
            ? string.Empty
            : $"{Environment.NewLine}Source: {request.SourceId}";
        return $"{description}{source}{Environment.NewLine}{Environment.NewLine}Arguments:{Environment.NewLine}{arguments}";
    }

    private void FocusTranscriptView()
    {
        if (TranscriptView.Visibility != Visibility.Visible)
        {
            return;
        }

        TranscriptView.Focus();
        Keyboard.Focus(TranscriptView);
        _terminalHostController.Focus();
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

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

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

