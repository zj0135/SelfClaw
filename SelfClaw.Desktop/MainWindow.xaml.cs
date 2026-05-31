using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.ViewModels;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;

namespace SelfClaw.Desktop;

public partial class MainWindow : Window
{
    private const double ExpandedRightPanelWidth = 320d;
    private const double ExpandedTerminalDrawerHeight = 286d;
    private static readonly Duration DrawerAnimationDuration = TimeSpan.FromMilliseconds(180);
    private const string AssetsHostName = "appassets.selfclaw.local";
    private const int MaxComposerImageAttachments = 6;
    private const long MaxComposerImageBytes = 10 * 1024 * 1024;
    private const int ComposerPreviewMaxEdge = 480;
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
        ConversationModes: [],
        SelectedConversationModeId: null,
        Profiles: [],
        SelectedProfileId: null,
        ProfileModels: [],
        SelectedProfileModel: null,
        WorkspaceRoots: [],
        SelectedWorkspaceRootId: null,
        ToolPermissionModes: [],
        SelectedToolPermissionModeId: null,
        ThemeOptions: [],
        SelectedThemeId: null,
        Channels: [],
        McpServers: [],
        Skills: [],
        AvailableMcpServers: [],
        AvailableSkills: [],
        SlashCommands: [],
        CommandFeedback: null,
        Agents: [],
        SelectedAgentId: null,
        AgentActivities: [],
        IsPlanningModeEnabled: false,
        PlanPanel: null,
        ContextUsage: new TranscriptContextUsage(0, DesktopSettings.DefaultModelContextWindow, DesktopSettings.DefaultModelAutoCompactTokenLimit, false),
        StatusText: string.Empty,
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
            conversations = state.Conversations,
            selectedConversationId = state.SelectedConversationId,
            theme = state.Theme,
            conversationModes = state.ConversationModes,
            selectedConversationModeId = state.SelectedConversationModeId,
            profiles = state.Profiles,
            selectedProfileId = state.SelectedProfileId,
            profileModels = state.ProfileModels,
            selectedProfileModel = state.SelectedProfileModel,
            workspaceRoots = state.WorkspaceRoots,
            selectedWorkspaceRootId = state.SelectedWorkspaceRootId,
            toolPermissionModes = state.ToolPermissionModes,
            selectedToolPermissionModeId = state.SelectedToolPermissionModeId,
            themeOptions = state.ThemeOptions,
            selectedThemeId = state.SelectedThemeId,
            channels = state.Channels,
            mcpServers = state.McpServers,
            skills = state.Skills,
            availableMcpServers = state.AvailableMcpServers,
            availableSkills = state.AvailableSkills,
            slashCommands = state.SlashCommands,
            commandFeedback = state.CommandFeedback,
            agents = state.Agents,
            selectedAgentId = state.SelectedAgentId,
            agentActivities = state.AgentActivities,
            isPlanningModeEnabled = state.IsPlanningModeEnabled,
            planPanel = state.PlanPanel,
            contextUsage = state.ContextUsage,
            statusText = state.StatusText,
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
        string? feedbackScope = null;

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
                case "new-conversation":
                    await _viewModel.CreateNewConversationFromUiAsync();
                    break;
                case "select-conversation":
                {
                    if (Guid.TryParse(document.RootElement.GetProperty("conversationId").GetString(), out var conversationId))
                    {
                        await _viewModel.SelectConversationAsync(conversationId);
                    }
                    break;
                }
                case "delete-conversation":
                {
                    var conversationId = ParseGuid(document.RootElement, "conversationId");
                    if (conversationId is Guid deleteId)
                    {
                        await _viewModel.DeleteConversationAsync(deleteId);
                    }
                    break;
                }
                case "select-agent":
                    await _viewModel.SetSelectedAgentAsync(GetString(document.RootElement, "agentId"));
                    break;
                case "assign-conversation-agent":
                    await AssignConversationAgentFromTranscriptAsync(document.RootElement);
                    break;
                case "send-prompt":
                {
                    var prompt = document.RootElement.GetProperty("prompt").GetString() ?? string.Empty;
                    var attachments = ParsePromptImageAttachments(document.RootElement);
                    var enableReasoning = GetBool(document.RootElement, "enableReasoning");
                    var profileModel = GetString(document.RootElement, "profileModel");
                    await _viewModel.SubmitPromptAsync(prompt, attachments, enableReasoning, profileModel);
                    break;
                }
                case "execute-command":
                {
                    var command = GetString(document.RootElement, "command");
                    var arguments = GetString(document.RootElement, "arguments");
                    var confirmed = GetBool(document.RootElement, "confirmed");
                    await _viewModel.ExecuteSlashCommandAsync(command, arguments, confirmed);
                    break;
                }
                case "clear-command-feedback":
                    _viewModel.ClearCommandFeedback();
                    break;
                case "stop-generation":
                    _viewModel.StopGeneration();
                    break;
                case "select-profile":
                {
                    if (Guid.TryParse(document.RootElement.GetProperty("profileId").GetString(), out var profileId))
                    {
                        await _viewModel.SetSelectedProfileAsync(profileId);
                    }
                    break;
                }
                case "select-profile-model":
                    await _viewModel.SetSelectedProfileModelAsync(GetString(document.RootElement, "profileModel"));
                    break;
                case "select-workspace":
                {
                    var rawId = document.RootElement.GetProperty("workspaceRootId").GetString();
                    Guid? workspaceRootId = Guid.TryParse(rawId, out var parsed) ? parsed : null;
                    await _viewModel.SetSelectedWorkspaceRootAsync(workspaceRootId);
                    break;
                }
                case "load-workspace-directory":
                    await LoadWorkspaceDirectoryFromTranscriptAsync(document.RootElement);
                    break;
                case "open-workspace-file":
                    await OpenWorkspaceFileFromTranscriptAsync(document.RootElement);
                    break;
                case "open-workspace-entry-location":
                    await OpenWorkspaceEntryLocationFromTranscriptAsync(document.RootElement);
                    break;
                case "select-tool-permission":
                    await _viewModel.SetToolPermissionModeAsync(document.RootElement.GetProperty("permissionModeId").GetString());
                    break;
                case "select-conversation-mode":
                    await _viewModel.SetConversationModeAsync(document.RootElement.GetProperty("modeId").GetString());
                    break;
                case "approve-tool-execution":
                {
                    var toolExecutionId = ParseGuid(document.RootElement, "toolExecutionId");
                    if (toolExecutionId is Guid approveId)
                    {
                        await _viewModel.ApproveToolExecutionAsync(approveId);
                    }
                    break;
                }
                case "reject-tool-execution":
                {
                    var toolExecutionId = ParseGuid(document.RootElement, "toolExecutionId");
                    if (toolExecutionId is Guid rejectId)
                    {
                        await _viewModel.RejectToolExecutionAsync(rejectId);
                    }
                    break;
                }
                case "select-theme":
                    await _viewModel.SetThemePreferenceAsync(document.RootElement.GetProperty("themeId").GetString());
                    break;
                case "set-plan-mode":
                    await _viewModel.SetPlanningModeAsync(GetBool(document.RootElement, "enabled"));
                    break;
                case "save-agent":
                    feedbackScope = "agent";
                    await SaveAgentFromTranscriptAsync(document.RootElement);
                    break;
                case "delete-agent":
                    feedbackScope = "agent";
                    await DeleteAgentFromTranscriptAsync(document.RootElement);
                    break;
                case "save-profile":
                    feedbackScope = "profile";
                    await SaveProfileFromTranscriptAsync(document.RootElement);
                    break;
                case "fetch-profile-models":
                    await FetchProfileModelsFromTranscriptAsync(document.RootElement);
                    break;
                case "delete-profile":
                    feedbackScope = "profile";
                    await DeleteProfileFromTranscriptAsync(document.RootElement);
                    break;
                case "save-workspace":
                    feedbackScope = "workspace";
                    await SaveWorkspaceFromTranscriptAsync(document.RootElement);
                    break;
                case "delete-workspace":
                    feedbackScope = "workspace";
                    await DeleteWorkspaceFromTranscriptAsync(document.RootElement);
                    break;
                case "save-channel":
                    feedbackScope = "channels";
                    await SaveChannelFromTranscriptAsync(document.RootElement);
                    break;
                case "toggle-channel":
                    feedbackScope = "channels";
                    await ToggleChannelFromTranscriptAsync(document.RootElement);
                    break;
                case "save-mcp-server":
                    feedbackScope = "mcp";
                    await SaveMcpServerFromTranscriptAsync(document.RootElement);
                    break;
                case "delete-mcp-server":
                    feedbackScope = "mcp";
                    await DeleteMcpServerFromTranscriptAsync(document.RootElement);
                    break;
                case "set-mcp-server-enabled":
                    feedbackScope = "mcp";
                    await SetMcpServerEnabledFromTranscriptAsync(document.RootElement);
                    break;
                case "set-skill-enabled":
                    feedbackScope = "skills";
                    await SetSkillEnabledFromTranscriptAsync(document.RootElement);
                    break;
                case "pick-workspace-path":
                    await PickWorkspacePathFromTranscriptAsync();
                    break;
                case "pick-composer-images":
                    await PickComposerImagesFromTranscriptAsync();
                    break;
                case "capture-composer-screenshot":
                    await CaptureComposerScreenshotFromTranscriptAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            PostUiFeedback("error", exception.Message, feedbackScope);
        }
    }

    private static IReadOnlyList<PromptImageAttachment> ParsePromptImageAttachments(JsonElement root)
    {
        if (!root.TryGetProperty("attachments", out var attachmentsElement) ||
            attachmentsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return attachmentsElement
            .EnumerateArray()
            .Select(item => new PromptImageAttachment(
                GetString(item, "sourcePath"),
                GetString(item, "fileName"),
                GetString(item, "mediaType"),
                GetLong(item, "byteLength")))
            .Where(item => !string.IsNullOrWhiteSpace(item.SourcePath))
            .Take(MaxComposerImageAttachments)
            .ToArray();
    }

    private async Task PickComposerImagesFromTranscriptAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.gif",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var attachments = dialog.FileNames
            .Take(MaxComposerImageAttachments)
            .Select(TryCreateComposerImagePayload)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

        await PostComposerImagesPickedAsync(attachments);
    }

    private async Task CaptureComposerScreenshotFromTranscriptAsync()
    {
        var restoreWindowState = WindowState;
        var shouldRestoreWindow = IsVisible;
        ScreenshotCaptureResult? capture = null;

        try
        {
            if (shouldRestoreWindow)
            {
                Hide();
                await Task.Delay(120);
            }

            capture = ScreenshotCaptureService.Capture();
        }
        finally
        {
            if (shouldRestoreWindow)
            {
                Show();
                WindowState = restoreWindowState;
                Activate();
            }
        }

        if (capture is null)
        {
            return;
        }

        var attachment = TryCreateComposerImagePayload(capture.FilePath);
        if (attachment is null)
        {
            TryDeleteFile(capture.FilePath);
            PostUiFeedback("error", "The screenshot is larger than 10 MB. Drag a smaller region and try again.");
            return;
        }

        await PostComposerImagesPickedAsync([attachment]);
    }

    private static object? TryCreateComposerImagePayload(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > MaxComposerImageBytes)
            {
                return null;
            }

            var mediaType = ResolveImageMediaType(fileInfo.FullName);
            if (mediaType is null)
            {
                return null;
            }

            return new
            {
                id = Guid.NewGuid().ToString("D"),
                sourcePath = fileInfo.FullName,
                fileName = fileInfo.Name,
                mediaType,
                byteLength = fileInfo.Length,
                dataUrl = TryCreateComposerImagePreviewDataUrl(fileInfo.FullName)
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryCreateComposerImagePreviewDataUrl(string path)
    {
        try
        {
            using var sourceImage = DrawingImage.FromFile(path);
            if (sourceImage.Width <= 0 || sourceImage.Height <= 0)
            {
                return null;
            }

            var scale = Math.Min(1d, (double)ComposerPreviewMaxEdge / Math.Max(sourceImage.Width, sourceImage.Height));
            var previewWidth = Math.Max(1, (int)Math.Round(sourceImage.Width * scale));
            var previewHeight = Math.Max(1, (int)Math.Round(sourceImage.Height * scale));

            using var previewBitmap = new DrawingBitmap(previewWidth, previewHeight, PixelFormat.Format32bppArgb);
            using (var graphics = DrawingGraphics.FromImage(previewBitmap))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(sourceImage, 0, 0, previewWidth, previewHeight);
            }

            using var stream = new MemoryStream();
            previewBitmap.Save(stream, ImageFormat.Png);
            return $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for oversized transient screenshots.
        }
    }

    private async Task PostComposerImagesPickedAsync(IReadOnlyList<object> attachments)
    {
        if (!_webViewReady || TranscriptView.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "composer-images-picked",
            attachments
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        if (await TryInvokeComposerImagesPickedBridgeAsync(payload))
        {
            return;
        }

        TranscriptView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private async Task<bool> TryInvokeComposerImagesPickedBridgeAsync(string payload)
    {
        if (TranscriptView.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            var script = $$"""
                (() => {
                    const payload = {{payload}};
                    if (typeof window.selfClawComposerImagesPicked === 'function') {
                        window.selfClawComposerImagesPicked(payload);
                        return true;
                    }

                    window.dispatchEvent(new CustomEvent('selfclaw-composer-images-picked', { detail: payload }));
                    return false;
                })()
                """;
            var result = await TranscriptView.CoreWebView2.ExecuteScriptAsync(script);
            return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task SaveProfileFromTranscriptAsync(JsonElement root)
    {
        var result = new ProfileEditorResult(
            ParseGuid(root, "profileId"),
            GetString(root, "name"),
            GetString(root, "endpoint"),
            GetString(root, "model"),
            GetBool(root, "temperatureEnabled"),
            GetDouble(root, "temperature", 0.7),
            GetBool(root, "topPEnabled"),
            GetDouble(root, "topP", 0.7),
            GetString(root, "apiKey"));

        await _viewModel.SaveProfileAsync(result);
        PostUiFeedback("success", "Profile saved.", "profile");
    }

    private async Task SaveAgentFromTranscriptAsync(JsonElement root)
    {
        var result = new DesktopAgentEditorResult(
            GetString(root, "originalAgentId"),
            GetString(root, "agentId"),
            GetString(root, "name"),
            GetString(root, "description"),
            ParseAgentExecutionMode(GetString(root, "mode")),
            GetString(root, "toolPolicy"),
            GetStringList(root, "skills"),
            GetStringList(root, "disabledSkills"),
            GetStringList(root, "mcpServers"),
            GetStringList(root, "disabledMcpServers"),
            GetString(root, "instructions"));

        await _viewModel.SaveAgentAsync(result);
        PostUiFeedback("success", "Agent saved.", "agent");
    }

    private async Task DeleteAgentFromTranscriptAsync(JsonElement root)
    {
        await _viewModel.DeleteAgentAsync(GetString(root, "agentId"));
        PostUiFeedback("success", "Agent deleted.", "agent");
    }

    private async Task AssignConversationAgentFromTranscriptAsync(JsonElement root)
    {
        var conversationId = ParseGuid(root, "conversationId");
        if (conversationId is Guid targetConversationId &&
            _viewModel.SelectedConversation?.Id != targetConversationId)
        {
            await _viewModel.SelectConversationAsync(targetConversationId);
        }

        await _viewModel.AssignSelectedConversationAgentAsync(GetString(root, "agentId"));
    }

    private async Task FetchProfileModelsFromTranscriptAsync(JsonElement root)
    {
        var requestId = GetInt(root, "requestId");

        try
        {
            var models = await _viewModel.FetchProfileModelsAsync(
                ParseGuid(root, "profileId"),
                GetString(root, "endpoint"),
                GetString(root, "apiKey"));

            PostProfileModelsFetched(requestId, models, null);
        }
        catch (Exception exception)
        {
            PostProfileModelsFetched(requestId, [], exception.Message);
        }
    }

    private async Task SaveWorkspaceFromTranscriptAsync(JsonElement root)
    {
        await _viewModel.SaveWorkspaceRootAsync(
            ParseGuid(root, "workspaceRootId"),
            GetString(root, "rootPath"),
            GetString(root, "name"));

        PostUiFeedback("success", "Workspace saved.", "workspace");
    }

    private async Task LoadWorkspaceDirectoryFromTranscriptAsync(JsonElement root)
    {
        var requestedWorkspaceRootId = ParseGuid(root, "workspaceRootId");
        var relativePath = GetString(root, "relativePath");
        var selectedWorkspace = _viewModel.SelectedWorkspaceRoot;

        if (requestedWorkspaceRootId is Guid requestedId &&
            selectedWorkspace?.Id != requestedId)
        {
            PostWorkspaceDirectoryLoaded(
                requestedId.ToString("D"),
                relativePath,
                [],
                null,
                null,
                "工作区已切换");
            return;
        }

        if (selectedWorkspace is null)
        {
            PostWorkspaceDirectoryLoaded(
                requestedWorkspaceRootId?.ToString("D"),
                relativePath,
                [],
                null,
                null,
                "未绑定工作区");
            return;
        }

        try
        {
            var entries = await _viewModel.ListSelectedWorkspaceFilesAsync(
                string.IsNullOrWhiteSpace(relativePath) ? null : relativePath);
            var visibleEntries = FilterWorkspaceTreeEntries(selectedWorkspace.RootPath, entries);

            PostWorkspaceDirectoryLoaded(
                selectedWorkspace.Id.ToString("D"),
                relativePath,
                visibleEntries,
                selectedWorkspace.Name,
                selectedWorkspace.RootPath,
                null);
        }
        catch (Exception exception)
        {
            PostWorkspaceDirectoryLoaded(
                selectedWorkspace.Id.ToString("D"),
                relativePath,
                [],
                selectedWorkspace.Name,
                selectedWorkspace.RootPath,
                exception.Message);
        }
    }

    private static IReadOnlyList<WorkspaceFileEntry> FilterWorkspaceTreeEntries(
        string workspaceRootPath,
        IReadOnlyList<WorkspaceFileEntry> entries)
    {
        return entries
            .Where(entry => !IsHiddenWorkspaceTreeEntry(workspaceRootPath, entry.RelativePath))
            .ToArray();
    }

    private static bool IsHiddenWorkspaceTreeEntry(string workspaceRootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var leafName = Path.GetFileName(relativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(leafName) &&
            leafName.StartsWith(".", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(workspaceRootPath, relativePath));
            return File.GetAttributes(fullPath).HasFlag(FileAttributes.Hidden);
        }
        catch
        {
            return false;
        }
    }

    private Task OpenWorkspaceFileFromTranscriptAsync(JsonElement root)
    {
        var fullPath = ResolveSelectedWorkspaceEntryPath(GetString(root, "relativePath"));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File was not found.", fullPath);
        }

        if (!TryOpenFileInVisualStudioCode(fullPath))
        {
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }

        return Task.CompletedTask;
    }

    private Task OpenWorkspaceEntryLocationFromTranscriptAsync(JsonElement root)
    {
        var fullPath = ResolveSelectedWorkspaceEntryPath(GetString(root, "relativePath"));
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("Workspace entry was not found.", fullPath);
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
        {
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    private string ResolveSelectedWorkspaceEntryPath(string relativePath)
    {
        var workspaceRoot = _viewModel.SelectedWorkspaceRoot
            ?? throw new InvalidOperationException("Select a workspace first.");

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A workspace path is required.");
        }

        var rootPath = Path.GetFullPath(workspaceRoot.RootPath);
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar) || rootPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? rootPath
            : $"{rootPath}{Path.DirectorySeparatorChar}";

        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Workspace path is out of range.");
        }

        return fullPath;
    }

    private static bool TryOpenFileInVisualStudioCode(string filePath)
    {
        foreach (var candidate in EnumerateVisualStudioCodeCandidates())
        {
            try
            {
                Process.Start(new ProcessStartInfo(candidate, $"\"{filePath}\"")
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                // Try the next VS Code candidate.
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateVisualStudioCodeCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            seen.Add(value);
        }

        AddCandidate(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Microsoft VS Code",
            "Code.exe"));
        AddCandidate(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft VS Code",
            "Code.exe"));
        AddCandidate(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft VS Code",
            "Code.exe"));
        AddCandidate("code");

        return seen;
    }

    private async Task DeleteProfileFromTranscriptAsync(JsonElement root)
    {
        var profileId = ParseGuid(root, "profileId");
        if (profileId is not Guid deleteId)
        {
            throw new InvalidOperationException("Profile id is required.");
        }

        await _viewModel.DeleteProfileAsync(deleteId);
        PostUiFeedback("success", "Profile deleted.", "profile");
    }

    private async Task DeleteWorkspaceFromTranscriptAsync(JsonElement root)
    {
        var workspaceRootId = ParseGuid(root, "workspaceRootId");
        if (workspaceRootId is not Guid deleteId)
        {
            throw new InvalidOperationException("Workspace id is required.");
        }

        await _viewModel.DeleteWorkspaceRootAsync(deleteId);
        PostUiFeedback("success", "Workspace deleted.", "workspace");
    }

    private async Task SaveChannelFromTranscriptAsync(JsonElement root)
    {
        var result = new ChannelEditorResult(
            GetString(root, "channelId"),
            GetString(root, "displayName"),
            ParseGuid(root, "profileId"),
            GetStringDictionary(root, "fieldValues"));

        await _viewModel.SaveChannelAsync(result);
        PostUiFeedback("success", "Channel saved.", "channels");
    }

    private async Task ToggleChannelFromTranscriptAsync(JsonElement root)
    {
        await _viewModel.SetChannelEnabledAsync(
            GetString(root, "channelId"),
            GetBool(root, "enabled"));

        PostUiFeedback(
            "success",
            GetBool(root, "enabled") ? "Channel started." : "Channel stopped.",
            "channels");
    }

    private async Task SaveMcpServerFromTranscriptAsync(JsonElement root)
    {
        var result = new McpServerEditorResult(
            GetString(root, "serverId"),
            GetString(root, "displayName"),
            GetBool(root, "enabled"),
            GetString(root, "command"),
            GetStringList(root, "args"),
            GetStringDictionary(root, "env"));

        await _viewModel.SaveMcpServerAsync(result);
        PostUiFeedback("success", "MCP server saved.", "mcp");
    }

    private async Task DeleteMcpServerFromTranscriptAsync(JsonElement root)
    {
        await _viewModel.DeleteMcpServerAsync(GetString(root, "serverId"));
        PostUiFeedback("success", "MCP server deleted.", "mcp");
    }

    private async Task SetMcpServerEnabledFromTranscriptAsync(JsonElement root)
    {
        var enabled = GetBool(root, "enabled");
        await _viewModel.SetMcpServerEnabledAsync(GetString(root, "serverId"), enabled);
        PostUiFeedback("success", enabled ? "MCP server enabled." : "MCP server disabled.", "mcp");
    }

    private async Task SetSkillEnabledFromTranscriptAsync(JsonElement root)
    {
        var enabled = GetBool(root, "enabled");
        await _viewModel.SetSkillEnabledAsync(GetString(root, "skillId"), enabled);
        PostUiFeedback("success", enabled ? "Skill enabled." : "Skill disabled.", "skills");
    }

    private static AgentExecutionMode ParseAgentExecutionMode(string? modeId)
        => string.Equals(modeId?.Trim(), "plan", StringComparison.OrdinalIgnoreCase)
            ? AgentExecutionMode.Plan
            : AgentExecutionMode.Direct;

    private Task PickWorkspacePathFromTranscriptAsync()
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            PostWorkspacePathPicked(dialog.FolderName);
        }

        return Task.CompletedTask;
    }

    private void PostWorkspacePathPicked(string folderPath)
    {
        if (!_webViewReady || TranscriptView.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "workspace-path-picked",
            rootPath = folderPath
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        TranscriptView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private void PostWorkspaceDirectoryLoaded(
        string? workspaceRootId,
        string? relativePath,
        IReadOnlyList<WorkspaceFileEntry> entries,
        string? workspaceName,
        string? workspaceRootPath,
        string? errorMessage)
    {
        if (!_webViewReady || TranscriptView.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "workspace-directory-loaded",
            workspaceRootId,
            relativePath = string.IsNullOrWhiteSpace(relativePath) ? null : relativePath,
            entries,
            workspaceName,
            workspaceRootPath,
            errorMessage
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        TranscriptView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private void PostUiFeedback(string level, string message, string? scope = null)
    {
        if (!_webViewReady || TranscriptView.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "settings-feedback",
            level,
            message,
            scope
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        TranscriptView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private void PostProfileModelsFetched(int requestId, IReadOnlyList<string> models, string? errorMessage)
    {
        if (!_webViewReady || TranscriptView.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "profile-models-fetched",
            requestId,
            models,
            errorMessage
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        TranscriptView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private static Guid? ParseGuid(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var raw = property.GetString();
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static string GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static long GetLong(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0;

    private static IReadOnlyDictionary<string, string> GetStringDictionary(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in property.EnumerateObject())
        {
            values[item.Name] = item.Value.ValueKind == JsonValueKind.String
                ? item.Value.GetString() ?? string.Empty
                : item.Value.GetRawText();
        }

        return values;
    }

    private static IReadOnlyList<string> GetStringList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : item.GetRawText())
            .ToArray();
    }

    private static double GetDouble(JsonElement root, string propertyName, double defaultValue)
        => root.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : defaultValue;

    private static bool GetBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) &&
           (property.ValueKind == JsonValueKind.True ||
            (property.ValueKind == JsonValueKind.False ? false : bool.TryParse(property.GetRawText(), out var value) && value));

    private static string? ResolveImageMediaType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null
        };

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

