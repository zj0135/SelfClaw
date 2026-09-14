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
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.Appearance;
using SelfClaw.Desktop.Services.Plugins;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Desktop.Services.Windowing;
using Microsoft.Extensions.Logging;

namespace SelfClaw.Desktop;

public sealed partial class MainWindow : Window
{
    private const string AssetsHostName = "appassets.selfclaw.local";
    private const string AttachmentHostName = "attachments.selfclaw.local";
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const uint MonitorDefaultToNearest = 2;
    private const double StartupWorkAreaMargin = 48d;
    private static readonly IntPtr HtCaption = new(2);

    private readonly MainWindowViewModel _viewModel;
    private readonly StoragePaths _storagePaths;
    private readonly PetActivityPresenter _petActivityPresenter;
    private readonly WebViewHostChannel _webViewHostChannel;
    private readonly WebViewMessageRouter _webViewMessageRouter;
    private readonly TerminalHostController _terminalHostController;
    private readonly PluginPanelHostController _pluginPanelHostController;
    private readonly AppearanceSettingsService _appearanceSettingsService;
    private readonly DesktopConversationActivationService _activation;
    private readonly ILogger<MainWindow> _logger;
    private Task? _initializationTask;

    internal MainWindow(
        MainWindowViewModel viewModel,
        PetActivityPresenter petActivityPresenter,
        WebViewHostChannel webViewHostChannel,
        WebViewMessageRouter webViewMessageRouter,
        TerminalHostController terminalHostController,
        PluginPanelHostController pluginPanelHostController,
        AppearanceSettingsService appearanceSettingsService,
        StoragePaths storagePaths,
        DesktopConversationActivationService activation,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        ApplyAdaptiveStartupSize();
        _viewModel = viewModel;
        _appearanceSettingsService = appearanceSettingsService;
        _storagePaths = storagePaths;
        _activation = activation;
        _logger = logger;
        _petActivityPresenter = petActivityPresenter;
        _webViewHostChannel = webViewHostChannel;
        _webViewMessageRouter = webViewMessageRouter;
        _terminalHostController = terminalHostController;
        _pluginPanelHostController = pluginPanelHostController;
        DataContext = viewModel;
        Loaded += OnLoadedAsync;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += HandlePreviewKeyDown;
        Closed += OnClosed;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        TranscriptView.NavigationCompleted += OnTranscriptNavigationCompleted;
        TranscriptView.NavigationStarting += OnTranscriptNavigationStarting;
        _petActivityPresenter.ConversationActivationRequested += OnPetConversationActivationRequested;
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
        => await InitializeAsync();

    internal Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
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
        _petActivityPresenter.ConversationActivationRequested -= OnPetConversationActivationRequested;

        if (TranscriptView.CoreWebView2 is not null)
        {
            TranscriptView.CoreWebView2.WebMessageReceived -= OnTranscriptWebMessageReceived;
        }

        _webViewHostChannel.Detach();
        TranscriptView.Dispose();

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.RemoveHook(WndProc);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowBackdropHelper.TryApplySystemBackdrop(this);
        // 缓存值来自上一次运行时前端推送的解析结果。WebView 还没加载，这是此刻能拿到的
        // 最好答案；前端起来后会经 ApplyCaptionTheme 命令校正。
        WindowBackdropHelper.TryApplyCaptionTheme(this, _appearanceSettingsService.CachedIsDark);

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
            _pluginPanelHostController.Attach(TranscriptView.CoreWebView2);

            var assetsRootPath = Path.Combine(AppContext.BaseDirectory, "Assets");
            var vueTranscriptPath = Path.Combine(assetsRootPath, "TranscriptVue", "index.html");

            if (!File.Exists(vueTranscriptPath))
            {
                throw new FileNotFoundException("Unable to locate the Vue transcript host page.", vueTranscriptPath);
            }

            // Injected into every document, so a plugin panel has window.selfclaw before its own script
            // runs and never has to ship a copy of the SDK. The script no-ops in the top frame.
            var pluginSdkPath = Path.Combine(assetsRootPath, "plugin-sdk.js");
            if (File.Exists(pluginSdkPath))
            {
                await TranscriptView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    await File.ReadAllTextAsync(pluginSdkPath));
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
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to initialize the WebView transcript host.");
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
    }

    private void OnTranscriptNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        => _webViewHostChannel.MarkNotReady();

    private async void HandlePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (await _terminalHostController.TryWriteEscapeAsync())
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

    private async Task ToggleTerminalToolAsync()
    {
        await SetTerminalDrawerOpenAsync(!_terminalHostController.IsOpen);
    }

    private async Task SetTerminalDrawerOpenAsync(bool isOpen)
    {
        await _terminalHostController.SetOpenAsync(isOpen, _viewModel.SelectedWorkspaceRootPath);
        if (isOpen)
        {
            FocusTranscriptView();
        }
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

    // 缩放热区做在网页四周而不是 WPF 侧：WebView2 铺满整个窗口，它是独立的子 HWND，鼠标消息直接进子
    // 窗口，父窗口在边缘既画不了东西也收不到输入。方位名到 HT* 码的映射留在宿主这边，网页只报方位。
    private void StartWindowResize(string? edge)
    {
        if (WindowState != WindowState.Normal
            || ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip))
        {
            return;
        }

        var hitTest = edge switch
        {
            "left" => HtLeft,
            "right" => HtRight,
            "top" => HtTop,
            "bottom" => HtBottom,
            "top-left" => HtTopLeft,
            "top-right" => HtTopRight,
            "bottom-left" => HtBottomLeft,
            "bottom-right" => HtBottomRight,
            _ => 0,
        };

        if (hitTest == 0)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, new IntPtr(hitTest), IntPtr.Zero);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => PostWindowState();

    private void PostWindowState()
        => _webViewHostChannel.PostPush(new
        {
            type = "window-state",
            isMaximized = WindowState == WindowState.Maximized
        });

    private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedWorkspaceRootPath) && _terminalHostController.IsOpen)
        {
            await _terminalHostController.UpdateWorkspaceRootAsync(_viewModel.SelectedWorkspaceRootPath);
        }
    }

    private async void OnTranscriptWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var command = await _webViewMessageRouter.RouteAsync(
                e.WebMessageAsJson,
                new WindowInteropHelper(this).Handle,
                e.Source);
            await ApplyWebViewHostCommandAsync(command);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "WebView message handling failed.");
        }
    }

    private async Task ApplyWebViewHostCommandAsync(WebViewHostCommand? command)
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
            case WebViewHostCommandKind.StartWindowResize:
                StartWindowResize(command.Value);
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
                await ToggleTerminalToolAsync();
                break;
            case WebViewHostCommandKind.ApplyCaptionTheme:
                WindowBackdropHelper.TryApplyCaptionTheme(
                    this,
                    string.Equals(command.Value, "dark", StringComparison.OrdinalIgnoreCase));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unsupported WebView host command.");
        }
    }

    private async void OnPetConversationActivationRequested(object? sender, Guid conversationId)
        => await _activation.ActivateAsync(conversationId);

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

        // 只约束「最大化」这一个状态：位置与尺寸对齐工作区，避免盖住任务栏。
        // ptMaxTrackSize 是用户拖拽的尺寸上限，而 WM_GETMINMAXINFO 在每次改变尺寸前都会到达，
        // 包括拖边框那一刻——在这里跟着写工作区大小，会把手动拖拽也一并钉死。留默认值。
        minMaxInfo.PtMaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
        minMaxInfo.PtMaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
        minMaxInfo.PtMaxSize.X = Math.Abs(workArea.Right - workArea.Left);
        minMaxInfo.PtMaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);

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

