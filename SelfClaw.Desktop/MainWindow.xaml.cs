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
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Core.Runtime;
using Forms = System.Windows.Forms;

namespace SelfClaw.Desktop;

public partial class MainWindow : Window
{
    private const double ExpandedRightPanelWidth = 320d;
    private static readonly Duration DrawerAnimationDuration = TimeSpan.FromMilliseconds(180);
    private const string AssetsHostName = "appassets.selfclaw.local";
    private const string AttachmentHostName = "attachments.selfclaw.local";
    private const int DefaultTerminalColumns = 120;
    private const int DefaultTerminalRows = 24;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcLButtonDown = 0x00A1;
    private const uint MonitorDefaultToNearest = 2;
    private const double StartupWorkAreaMargin = 48d;
    private static readonly IntPtr HtCaption = new(2);

    private readonly MainWindowViewModel _viewModel;
    private readonly StoragePaths _storagePaths;
    private readonly ProgrammingAssistantSettingsService _programmingAssistantSettingsService;
    private readonly AiProviderSettingsBridge _aiProviderSettingsBridge;
    private readonly PetHost _petHost;
    private readonly PetActivityPresenter _petActivityPresenter;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly DesktopNotificationService _desktopNotificationService;
    private TranscriptRenderState _pendingTranscript = new(
        Items: [],
        AutoScroll: false,
        Conversations: [],
        SelectedConversationId: null,
        IsBusy: false);
    private bool _webViewReady;
    private bool _isTerminalDrawerOpen;
    private bool _terminalReady;
    private bool _isTerminalFocused;
    private bool _isRightPanelOpen;
    private bool _isSystemSettingsOpen;
    private string? _activeRightPanelTool;
    private DispatcherTimer? _rightPanelAnimationTimer;
    private ConPtyTerminalSession? _terminalSession;
    private string _terminalWorkingDirectory = ResolveDefaultTerminalWorkingDirectory();
    private int _terminalColumns = DefaultTerminalColumns;
    private int _terminalRows = DefaultTerminalRows;
    private Guid? _currentApprovalId;

    public MainWindow(
        MainWindowViewModel viewModel,
        DesktopNotificationService desktopNotificationService,
        DesktopToolApprovalHandler toolApprovalHandler,
        ProgrammingAssistantSettingsService programmingAssistantSettingsService,
        AiProviderSettingsBridge aiProviderSettingsBridge,
        PetHost petHost,
        PetActivityPresenter petActivityPresenter,
        AgentActivityCoordinator agentActivityCoordinator,
        StoragePaths storagePaths)
    {
        InitializeComponent();
        ApplyAdaptiveStartupSize();
        _viewModel = viewModel;
        _storagePaths = storagePaths;
        _programmingAssistantSettingsService = programmingAssistantSettingsService;
        _aiProviderSettingsBridge = aiProviderSettingsBridge;
        _petHost = petHost;
        _petActivityPresenter = petActivityPresenter;
        _agentActivityCoordinator = agentActivityCoordinator;
        _toolApprovalHandler = toolApprovalHandler;
        _desktopNotificationService = desktopNotificationService;
        DataContext = viewModel;
        Loaded += OnLoadedAsync;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += HandlePreviewKeyDown;
        Closed += OnClosed;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        TranscriptView.NavigationCompleted += OnTranscriptNavigationCompleted;
        _aiProviderSettingsBridge.ResponseReady += OnAiProviderBridgeResponseReady;
        _aiProviderSettingsBridge.ModelSelectionChanged += OnModelSelectionChanged;
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
        _viewModel.TranscriptChanged -= OnTranscriptChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        StateChanged -= OnWindowStateChanged;
        TranscriptView.NavigationCompleted -= OnTranscriptNavigationCompleted;
        _aiProviderSettingsBridge.ResponseReady -= OnAiProviderBridgeResponseReady;
        _aiProviderSettingsBridge.ModelSelectionChanged -= OnModelSelectionChanged;
        _toolApprovalHandler.ApprovalRequested -= OnToolApprovalRequested;
        _toolApprovalHandler.ApprovalExpired -= OnToolApprovalExpired;
        _agentActivityCoordinator.SnapshotChanged -= OnAgentActivitySnapshotChanged;
        _petActivityPresenter.ConversationActivationRequested -= OnPetConversationActivationRequested;
        _toolApprovalHandler.RejectAll();
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

        _webViewReady = true;
        PostTranscript(_pendingTranscript);
        PostTerminalState();
        PostWindowState();
        RepostCurrentApproval();
    }

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
            conversations = state.Conversations,
            selectedConversationId = state.SelectedConversationId,
            isBusy = state.IsBusy,
            activityText = state.ActivityText
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

            _viewModel.StopSelectedConversation();
        }
    }

    private void OnFallbackCloseButtonClick(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleTerminalTool()
    {
        SetSystemSettingsOpen(false);
        SetTerminalDrawerOpen(!_isTerminalDrawerOpen);
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
        => PostTerminalMessage(new
        {
            type = _isSystemSettingsOpen ? "show-settings" : "hide-settings"
        });

    private void OnSettingsClosedFromWebView()
    {
        SetSystemSettingsOpen(false);
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
        => PostTerminalMessage(new
        {
            type = "window-state",
            isMaximized = WindowState == WindowState.Maximized
        });

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedWorkspaceRootPath) && _isTerminalDrawerOpen)
        {
            ResetTerminalWorkingDirectoryIfNeeded();
        }
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

            var type = typeElement.GetString();
            if (type is not null && await _aiProviderSettingsBridge.TryHandleAsync(type, document.RootElement))
            {
                return;
            }

            switch (type)
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
                case "stop-generation":
                    _viewModel.StopSelectedConversation();
                    break;
                case "resolve-tool-approval":
                {
                    var toolExecutionId = document.RootElement.TryGetProperty("toolExecutionId", out var toolExecutionIdElement)
                        ? toolExecutionIdElement.GetString()
                        : null;
                    var approved = document.RootElement.TryGetProperty("approved", out var approvedElement) &&
                                   approvedElement.GetBoolean();
                    if (Guid.TryParse(toolExecutionId, out var parsedToolExecutionId))
                    {
                        _agentActivityCoordinator.TryResolveApproval(parsedToolExecutionId, approved);
                    }
                    break;
                }
                case "new-chat":
                    _viewModel.StartNewConversation();
                    break;
                case "select-conversation":
                {
                    var conversationId = document.RootElement.TryGetProperty("conversationId", out var conversationIdElement)
                        ? conversationIdElement.GetString()
                        : null;
                    if (Guid.TryParse(conversationId, out var parsedConversationId))
                    {
                        _viewModel.SelectConversation(parsedConversationId);
                    }
                    break;
                }
                case "delete-conversation":
                {
                    var conversationId = document.RootElement.TryGetProperty("conversationId", out var conversationIdElement)
                        ? conversationIdElement.GetString()
                        : null;
                    if (Guid.TryParse(conversationId, out var parsedConversationId))
                    {
                        await _viewModel.DeleteConversationAsync(parsedConversationId);
                    }
                    break;
                }
                case "clear-conversations":
                {
                    if (document.RootElement.TryGetProperty("conversationIds", out var conversationIdsElement) &&
                        conversationIdsElement.ValueKind == JsonValueKind.Array)
                    {
                        var conversationIds = conversationIdsElement
                            .EnumerateArray()
                            .Select(item => item.GetString())
                            .Where(item => Guid.TryParse(item, out _))
                            .Select(item => Guid.Parse(item!))
                            .ToArray();
                        await _viewModel.DeleteConversationsAsync(conversationIds);
                    }
                    break;
                }
                case "delete-workspace-root":
                {
                    var workspaceRootId = document.RootElement.TryGetProperty("workspaceRootId", out var workspaceRootIdElement)
                        ? workspaceRootIdElement.GetString()
                        : null;
                    if (Guid.TryParse(workspaceRootId, out var parsedWorkspaceRootId))
                    {
                        await _viewModel.DeleteWorkspaceRootAsync(parsedWorkspaceRootId);
                    }
                    break;
                }
                case "window-drag":
                    StartWindowDrag();
                    break;
                case "window-minimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "window-toggle-maximize":
                    ToggleWindowState();
                    break;
                case "window-close":
                    Close();
                    break;
                case "toggle-terminal":
                    ToggleTerminalTool();
                    break;
                case "toggle-files":
                    ToggleFileManagerTool();
                    break;
                case "toggle-browser":
                    ToggleBrowserTool();
                    break;
                case "get-workspace-selection":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    var refresh = document.RootElement.TryGetProperty("refresh", out var refreshElement) &&
                                  refreshElement.GetBoolean();
                    await PostWorkspaceSelectionAsync(requestId, refresh);
                    break;
                }
                case "select-workspace-root":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    var workspaceRootId = document.RootElement.TryGetProperty("workspaceRootId", out var workspaceRootIdElement)
                        ? workspaceRootIdElement.GetString()
                        : null;
                    var rootPath = document.RootElement.TryGetProperty("rootPath", out var rootPathElement)
                        ? rootPathElement.GetString()
                        : null;
                    await SelectWorkspaceRootAsync(requestId, workspaceRootId, rootPath);
                    break;
                }
                case "browse-workspace-folder":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    await BrowseWorkspaceFolderAsync(requestId);
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
                case "settings-closed":
                    OnSettingsClosedFromWebView();
                    break;
                case "scan-programming-clis":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    await PostProgrammingAssistantSettingsAsync(requestId, refresh: true);
                    break;
                }
                case "get-programming-assistant-settings":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    await PostProgrammingAssistantSettingsAsync(requestId, refresh: false);
                    break;
                }
                case "select-programming-cli":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    var cliId = document.RootElement.TryGetProperty("cliId", out var cliIdElement)
                        ? cliIdElement.GetString()
                        : null;
                    await SelectProgrammingCliAsync(requestId, cliId);
                    break;
                }
                case "select-programming-model":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    var model = document.RootElement.TryGetProperty("model", out var modelElement)
                        ? modelElement.GetString()
                        : null;
                    await SelectProgrammingModelAsync(requestId, model);
                    break;
                }
                case "select-programming-reasoning":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    var reasoningLevel = document.RootElement.TryGetProperty("reasoningLevel", out var reasoningElement)
                        ? reasoningElement.GetString()
                        : null;
                    await SelectProgrammingReasoningAsync(requestId, reasoningLevel);
                    break;
                }
                case "select-composer-mode":
                {
                    var mode = document.RootElement.TryGetProperty("mode", out var modeElement)
                        ? modeElement.GetString()
                        : null;
                    await _viewModel.SelectComposerModeAsync(mode);
                    break;
                }
                case "test-programming-cli":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    var cliId = document.RootElement.TryGetProperty("cliId", out var cliIdElement)
                        ? cliIdElement.GetString()
                        : null;
                    await TestProgrammingCliAsync(requestId, cliId);
                    break;
                }
                case "get-pet-settings":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    await PostPetSettingsAsync(requestId);
                    break;
                }
                case "set-pet-visible":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    var enabled = document.RootElement.TryGetProperty("enabled", out var enabledElement) &&
                                  enabledElement.GetBoolean();
                    await SetPetVisibleAsync(requestId, enabled);
                    break;
                }
                case "select-builtin-pet":
                {
                    var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    var petId = document.RootElement.TryGetProperty("petId", out var petIdElement)
                        ? petIdElement.GetString()
                        : null;
                    await SelectBuiltInPetAsync(requestId, petId);
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private async Task PostProgrammingAssistantSettingsAsync(string? requestId, bool refresh)
    {
        try
        {
            var settings = refresh
                ? await _programmingAssistantSettingsService.RescanAsync()
                : await _programmingAssistantSettingsService.GetCurrentAsync();
            PostProgrammingAssistantSettings(requestId, settings);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostWebMessage(new
            {
                type = "programming-assistant-settings",
                requestId,
                tools = Array.Empty<DetectedProgrammingCli>(),
                selectedCliId = (string?)null,
                error = exception.Message
            });
        }
    }

    private async Task SelectProgrammingCliAsync(string? requestId, string? cliId)
    {
        try
        {
            var settings = await _programmingAssistantSettingsService.SelectCliAsync(cliId);
            PostProgrammingAssistantSettings(requestId, settings);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostWebMessage(new
            {
                type = "programming-assistant-settings",
                requestId,
                tools = Array.Empty<DetectedProgrammingCli>(),
                selectedCliId = (string?)null,
                error = exception.Message
            });
        }
    }

    private async Task SelectProgrammingModelAsync(string? requestId, string? model)
    {
        try
        {
            var settings = await _programmingAssistantSettingsService.SelectModelAsync(model);
            PostProgrammingAssistantSettings(requestId, settings);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostWebMessage(new
            {
                type = "programming-assistant-settings",
                requestId,
                tools = Array.Empty<DetectedProgrammingCli>(),
                selectedCliId = (string?)null,
                error = exception.Message
            });
        }
    }

    private async Task SelectProgrammingReasoningAsync(string? requestId, string? reasoningLevel)
    {
        try
        {
            var settings = await _programmingAssistantSettingsService.SelectReasoningLevelAsync(reasoningLevel);
            PostProgrammingAssistantSettings(requestId, settings);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostWebMessage(new
            {
                type = "programming-assistant-settings",
                requestId,
                tools = Array.Empty<DetectedProgrammingCli>(),
                selectedCliId = (string?)null,
                error = exception.Message
            });
        }
    }

    private async Task TestProgrammingCliAsync(string? requestId, string? cliId)
    {
        CliTestResult result;
        try
        {
            result = await _programmingAssistantSettingsService.TestCliAsync(cliId);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            result = new CliTestResult(cliId ?? string.Empty, Success: false, Version: null, Error: exception.Message);
        }

        PostWebMessage(new
        {
            type = "programming-cli-test-result",
            requestId,
            cliId = result.CliId,
            success = result.Success,
            version = result.Version,
            error = result.Error
        });
    }

    private void PostProgrammingAssistantSettings(string? requestId, ProgrammingAssistantSettings settings)
        => PostWebMessage(new
        {
            type = "programming-assistant-settings",
            requestId,
            selectedCliId = settings.SelectedCliId,
            selectedModel = settings.SelectedModel,
            selectedReasoningLevel = settings.SelectedReasoningLevel,
            tools = settings.Tools,
            scannedAtUtc = settings.ScannedAtUtc
        });

    private async Task PostWorkspaceSelectionAsync(string? requestId, bool refresh)
    {
        try
        {
            if (refresh || _viewModel.WorkspaceRoots.Count == 0)
            {
                await _viewModel.ReloadWorkspaceSelectionAsync();
            }

            PostWorkspaceSelection(requestId);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostWorkspaceSelection(requestId, error: exception.Message);
        }
    }

    private async Task SelectWorkspaceRootAsync(string? requestId, string? workspaceRootId, string? rootPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                await _viewModel.SelectOrAddWorkspaceRootAsync(rootPath);
            }
            else if (Guid.TryParse(workspaceRootId, out var parsedWorkspaceRootId))
            {
                _viewModel.SelectWorkspaceRoot(parsedWorkspaceRootId);
            }
            else
            {
                _viewModel.SelectWorkspaceRoot(null);
            }

            PostWorkspaceSelection(requestId);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostWorkspaceSelection(requestId, error: exception.Message);
        }
    }

    private async Task BrowseWorkspaceFolderAsync(string? requestId)
    {
        try
        {
            var initialDirectory = ResolveInitialWorkspacePickerDirectory();
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "选择工作目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                InitialDirectory = initialDirectory
            };

            var owner = new WindowHandleWrapper(new WindowInteropHelper(this).Handle);
            var result = dialog.ShowDialog(owner);
            if (result == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                await _viewModel.SelectOrAddWorkspaceRootAsync(dialog.SelectedPath);
                PostWorkspaceSelection(requestId);
                return;
            }

            PostWorkspaceSelection(requestId, cancelled: true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostWorkspaceSelection(requestId, error: exception.Message);
        }
    }

    private void PostWorkspaceSelection(string? requestId, bool? cancelled = null, string? error = null)
    {
        var selected = _viewModel.SelectedWorkspaceRoot;
        var currentPath = selected?.RootPath;
        var currentIsFallback = false;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            currentPath = ResolveDefaultTerminalWorkingDirectory();
            currentIsFallback = true;
        }

        PostWebMessage(new
        {
            type = "workspace-selection",
            requestId,
            cancelled,
            error,
            current = new
            {
                id = selected?.Id.ToString("D"),
                name = selected?.Name ?? ResolveDirectoryDisplayName(currentPath),
                path = currentPath,
                isFallback = currentIsFallback
            },
            roots = _viewModel.WorkspaceRoots
                .Select(root => new
                {
                    id = root.Id.ToString("D"),
                    name = root.Name,
                    path = root.RootPath,
                    selected = selected?.Id == root.Id
                })
                .ToArray(),
            commonFolders = BuildCommonWorkspaceFolders()
        });
    }

    private object[] BuildCommonWorkspaceFolders()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath)
            ? Array.Empty<object>()
            : new object[]
            {
                new
                {
                    id = "desktop",
                    name = "Desktop",
                    path = desktopPath
                }
            };
    }

    private string ResolveInitialWorkspacePickerDirectory()
    {
        var selectedPath = _viewModel.SelectedWorkspaceRootPath;
        if (!string.IsNullOrWhiteSpace(selectedPath) && Directory.Exists(selectedPath))
        {
            return selectedPath;
        }

        return ResolveDefaultTerminalWorkingDirectory();
    }

    private static string ResolveDirectoryDisplayName(string path)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(normalizedPath);
        return string.IsNullOrWhiteSpace(name) ? normalizedPath : name;
    }

    private async Task PostPetSettingsAsync(string? requestId)
    {
        try
        {
            var state = await _petHost.GetStateAsync();
            PostPetSettings(requestId, state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostPetSettingsError(requestId, exception);
        }
    }

    private async Task SetPetVisibleAsync(string? requestId, bool enabled)
    {
        try
        {
            var command = new PetHostCommand(
                enabled ? PetHostCommandKind.Show : PetHostCommandKind.Hide);
            var state = await _petHost.ExecuteAsync(command);
            PostPetSettings(requestId, state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostPetSettingsError(requestId, exception);
        }
    }

    private async Task SelectBuiltInPetAsync(string? requestId, string? petId)
    {
        try
        {
            var state = await _petHost.ExecuteAsync(
                new PetHostCommand(PetHostCommandKind.SelectBuiltInPet, petId));
            PostPetSettings(requestId, state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            PostPetSettingsError(requestId, exception);
        }
    }

    private void PostPetSettings(string? requestId, PetHostState state)
    {
        PostWebMessage(new
        {
            type = "pet-settings",
            requestId,
            enabled = state.Settings.Enabled,
            selectedPetId = state.SelectedBuiltInPetId,
            spriteSheetPath = state.Settings.SpriteSheetPath,
            pets = state.BuiltInPackages.Select(package => new
            {
                package.Id,
                package.DisplayName,
                package.Description,
                package.Author,
                package.Tags,
                package.Source,
                package.SourceUrl,
                previewSrc = $"https://{AssetsHostName}/{package.PreviewAssetPath}",
                cols = package.Columns,
                rows = package.Rows,
            }).ToArray(),
        });
    }

    private void PostPetSettingsError(string? requestId, Exception exception)
        => PostWebMessage(new
        {
            type = "pet-settings",
            requestId,
            enabled = false,
            selectedPetId = (string?)null,
            spriteSheetPath = (string?)null,
            error = exception.Message
        });

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
        // Push-style messages wait for the initial navigation so they are not lost on a page
        // that has not attached listeners yet.
        if (!_webViewReady)
        {
            return;
        }

        PostWebMessage(payload);
    }

    /// <summary>
    /// Posts without the <c>_webViewReady</c> gate. Responses to a received web message must use this:
    /// the request proves the page's script is already running (its listeners exist), and during app
    /// startup that happens before <c>NavigationCompleted</c> flips <c>_webViewReady</c> — gating the
    /// response would silently drop it and leave the requester waiting forever.
    /// </summary>
    private void PostWebMessage(object payload)
    {
        if (TranscriptView.CoreWebView2 is null)
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

    private void OnModelSelectionChanged(Guid? modelProfileId)
        => _viewModel.SelectModelProfile(modelProfileId);

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
        => PostWebMessage(new
        {
            type = "toolApprovalRequest",
            toolExecutionId = request.ToolExecutionId.ToString(),
            toolName = request.ToolName,
            displayName = request.DisplayName,
            description = request.Description,
            argumentsJson = request.ArgumentsJson
        });

    private void PostToolApprovalClear()
        => PostWebMessage(new { type = "toolApprovalClear" });

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
            _viewModel.SelectConversation(conversationId);
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
        return $"{description}{Environment.NewLine}{Environment.NewLine}Arguments:{Environment.NewLine}{arguments}";
    }

    private void OnAiProviderBridgeResponseReady(object payload)
        => PostWebMessage(payload);

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

    private sealed class WindowHandleWrapper : Forms.IWin32Window
    {
        public WindowHandleWrapper(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}

