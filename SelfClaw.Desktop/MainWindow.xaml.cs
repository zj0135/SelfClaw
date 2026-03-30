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
    private readonly MainWindowViewModel _viewModel;
    private TranscriptRenderState _pendingTranscript = new([], false, [], null, "light", [], null, null, [], null, [], null, [], null, [], string.Empty, false);
    private bool _webViewReady;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoadedAsync;
        SourceInitialized += (_, _) => WindowBackdropHelper.TryApplyMica(this);
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
            TranscriptView.Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Transcript", "transcript.html"));
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
            profiles = state.Profiles,
            selectedProfileId = state.SelectedProfileId,
            selectedProfileModel = state.SelectedProfileModel,
            workspaceRoots = state.WorkspaceRoots,
            selectedWorkspaceRootId = state.SelectedWorkspaceRootId,
            toolPermissionModes = state.ToolPermissionModes,
            selectedToolPermissionModeId = state.SelectedToolPermissionModeId,
            themeOptions = state.ThemeOptions,
            selectedThemeId = state.SelectedThemeId,
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
                    await SaveProfileFromTranscriptAsync(document.RootElement);
                    break;
                case "save-workspace":
                    await SaveWorkspaceFromTranscriptAsync(document.RootElement);
                    break;
                case "pick-workspace-path":
                    await PickWorkspacePathFromTranscriptAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            PostUiFeedback("error", exception.Message);
        }
    }

    private async Task SaveProfileFromTranscriptAsync(JsonElement root)
    {
        var result = new ProfileEditorResult(
            ParseGuid(root, "profileId"),
            GetString(root, "name"),
            GetString(root, "endpoint"),
            GetString(root, "model"),
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
}
