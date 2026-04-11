using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.ViewModels;

namespace SelfClaw.Desktop;

public partial class MainWindow : Window
{
    private const string AssetsHostName = "appassets.selfclaw.local";

    private readonly MainWindowViewModel _viewModel;
    private TranscriptRenderState _pendingTranscript = new([], false, [], null, "light", [], null, [], null, null, [], null, [], null, [], null, [], null, [], null, [], [], [], string.Empty, false);
    private bool _webViewReady;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoadedAsync;
        SourceInitialized += (_, _) => WindowBackdropHelper.TryApplySystemBackdrop(this);
        PreviewKeyDown += HandlePreviewKeyDown;
        Closed += OnClosed;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        TranscriptView.NavigationCompleted += OnTranscriptNavigationCompleted;
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
            selectedProfileModel = state.SelectedProfileModel,
            workspaceRoots = state.WorkspaceRoots,
            selectedWorkspaceRootId = state.SelectedWorkspaceRootId,
            toolPermissionModes = state.ToolPermissionModes,
            selectedToolPermissionModeId = state.SelectedToolPermissionModeId,
            teamRoundModes = state.TeamRoundModes,
            selectedTeamRoundModeId = state.SelectedTeamRoundModeId,
            teamOutputModes = state.TeamOutputModes,
            selectedTeamOutputModeId = state.SelectedTeamOutputModeId,
            themeOptions = state.ThemeOptions,
            selectedThemeId = state.SelectedThemeId,
            channels = state.Channels,
            teamMembers = state.TeamMembers,
            agentActivities = state.AgentActivities,
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
                case "send-prompt":
                {
                    var prompt = document.RootElement.GetProperty("prompt").GetString() ?? string.Empty;
                    await _viewModel.SubmitPromptAsync(prompt);
                    break;
                }
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
                case "select-workspace":
                {
                    var rawId = document.RootElement.GetProperty("workspaceRootId").GetString();
                    Guid? workspaceRootId = Guid.TryParse(rawId, out var parsed) ? parsed : null;
                    await _viewModel.SetSelectedWorkspaceRootAsync(workspaceRootId);
                    break;
                }
                case "select-tool-permission":
                    await _viewModel.SetToolPermissionModeAsync(document.RootElement.GetProperty("permissionModeId").GetString());
                    break;
                case "select-conversation-mode":
                    await _viewModel.SetConversationModeAsync(document.RootElement.GetProperty("modeId").GetString());
                    break;
                case "select-team-max-rounds":
                    await _viewModel.SetTeamMaxRoundsAsync(document.RootElement.GetProperty("roundsId").GetString());
                    break;
                case "select-team-output-mode":
                    await _viewModel.SetTeamOutputModeAsync(document.RootElement.GetProperty("outputModeId").GetString());
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
                case "save-profile":
                    feedbackScope = "profile";
                    await SaveProfileFromTranscriptAsync(document.RootElement);
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
                case "pick-workspace-path":
                    await PickWorkspacePathFromTranscriptAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            PostUiFeedback("error", exception.Message, feedbackScope);
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

    private async Task SaveWorkspaceFromTranscriptAsync(JsonElement root)
    {
        await _viewModel.SaveWorkspaceRootAsync(
            ParseGuid(root, "workspaceRootId"),
            GetString(root, "rootPath"),
            GetString(root, "name"));

        PostUiFeedback("success", "Workspace saved.", "workspace");
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
            GetString(root, "appId"),
            GetString(root, "botDisplayName"),
            ParseGuid(root, "profileId"),
            GetString(root, "appSecret"));

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

    private static double GetDouble(JsonElement root, string propertyName, double defaultValue)
        => root.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : defaultValue;

    private static bool GetBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) &&
           (property.ValueKind == JsonValueKind.True ||
            (property.ValueKind == JsonValueKind.False ? false : bool.TryParse(property.GetRawText(), out var value) && value));
}

