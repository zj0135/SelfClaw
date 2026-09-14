using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services.Agents;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Desktop.Services.Appearance;
using SelfClaw.Desktop.Services.Extensions;
using SelfClaw.Desktop.Services.Git;
using SelfClaw.Desktop.Services.Plugins;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.ViewModels;

namespace SelfClaw.Desktop.Services.WebView;

internal sealed class WebViewMessageRouter : IDisposable
{
    public const string ApplicationHostName = "appassets.selfclaw.local";

    private readonly AiProviderSettingsBridge _aiProviderSettingsBridge;
    private readonly ExtensionSettingsBridge _extensionSettingsBridge;
    private readonly AgentSettingsBridge _agentSettingsBridge;
    private readonly ProgrammingAssistantSettingsBridge _programmingAssistantSettingsBridge;
    private readonly AppearanceSettingsBridge _appearanceSettingsBridge;
    private readonly PetSettingsBridge _petSettingsBridge;
    private readonly WorkspaceSelectionBridge _workspaceSelectionBridge;
    private readonly GitWorkspaceBridge? _gitWorkspaceBridge;
    private readonly TerminalHostController _terminalHostController;
    private readonly PluginPanelHostController _pluginPanelHostController;
    private readonly PluginPanelBridge _pluginPanelBridge;
    private readonly MainWindowViewModel _viewModel;
    private readonly ToolApprovalPresenter _approvals;
    private readonly WebViewHostChannel _hostChannel;
    private readonly TranscriptDelivery _transcriptDelivery;
    private readonly ActivityPanelBridge? _activityPanelBridge;
    private int _disposeStarted;
    private readonly ILogger<WebViewMessageRouter> _logger;
    private readonly object _lifecycleGate = new();
    private int _activeRoutes;
    private bool _stopping;
    private TaskCompletionSource? _drained;

    public WebViewMessageRouter(
        AiProviderSettingsBridge aiProviderSettingsBridge,
        ExtensionSettingsBridge extensionSettingsBridge,
        AgentSettingsBridge agentSettingsBridge,
        ProgrammingAssistantSettingsBridge programmingAssistantSettingsBridge,
        AppearanceSettingsBridge appearanceSettingsBridge,
        PetSettingsBridge petSettingsBridge,
        WorkspaceSelectionBridge workspaceSelectionBridge,
        TerminalHostController terminalHostController,
        PluginPanelHostController pluginPanelHostController,
        PluginPanelBridge pluginPanelBridge,
        MainWindowViewModel viewModel,
        ToolApprovalPresenter approvals,
        WebViewHostChannel hostChannel,
        TranscriptDelivery transcriptDelivery,
        GitWorkspaceBridge? gitWorkspaceBridge = null,
        ActivityPanelBridge? activityPanelBridge = null,
        ILogger<WebViewMessageRouter>? logger = null)
    {
        _aiProviderSettingsBridge = aiProviderSettingsBridge;
        _extensionSettingsBridge = extensionSettingsBridge;
        _agentSettingsBridge = agentSettingsBridge;
        _programmingAssistantSettingsBridge = programmingAssistantSettingsBridge;
        _appearanceSettingsBridge = appearanceSettingsBridge;
        _petSettingsBridge = petSettingsBridge;
        _workspaceSelectionBridge = workspaceSelectionBridge;
        _gitWorkspaceBridge = gitWorkspaceBridge;
        _terminalHostController = terminalHostController;
        _pluginPanelHostController = pluginPanelHostController;
        _pluginPanelBridge = pluginPanelBridge;
        _viewModel = viewModel;
        _approvals = approvals;
        _hostChannel = hostChannel;
        _transcriptDelivery = transcriptDelivery;
        _activityPanelBridge = activityPanelBridge;
        _logger = logger ?? NullLogger<WebViewMessageRouter>.Instance;

    }

    public async Task<WebViewHostCommand?> RouteAsync(
        string messageJson,
        nint ownerHandle,
        string? sourceUri = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson) || !IsApplicationOrigin(sourceUri))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(messageJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            lock (_lifecycleGate)
            {
                if (_stopping)
                {
                    PostFailure(document.RootElement, "shutting-down", "The application is shutting down.");
                    return null;
                }
                _activeRoutes++;
            }
            try
            {
                return await RouteDocumentAsync(document.RootElement, ownerHandle, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                _logger.LogError(exception, "WebView command {Command} failed. RequestId={RequestId}",
                    ReadOptionalString(document.RootElement, "type"), ReadOptionalString(document.RootElement, "requestId"));
                PostFailure(document.RootElement, exception is ArgumentException ? "invalid-request" : "operation-failed", exception.Message);
                return null;
            }
            finally
            {
                lock (_lifecycleGate)
                    if (--_activeRoutes == 0) _drained?.TrySetResult();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            _stopping = true;
            return _activeRoutes == 0 ? Task.CompletedTask :
                (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);
        }
    }

    private void PostFailure(JsonElement payload, string errorCode, string error)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        _hostChannel.PostResponse(new { type = "operation-result", requestId = ReadOptionalString(payload, "requestId"),
            command = ReadOptionalString(payload, "type"), ok = false, errorCode, error });
    }

    // Every message type below acts on the user's behalf — sending prompts, deleting extensions, closing
    // the window. Plugin panels run in cross-origin iframes inside the same WebView2, so the router
    // cannot assume a message came from the application shell. Identity is the frame's own origin, which
    // the page cannot forge; anything else is dropped before `type` is even read. This holds regardless
    // of whether WebView2 exposes chrome.webview inside iframes.
    public static bool IsApplicationOrigin(string? sourceUri)
        => sourceUri is not null &&
           Uri.TryCreate(sourceUri, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           uri.IsDefaultPort &&
           string.Equals(uri.Host, ApplicationHostName, StringComparison.OrdinalIgnoreCase);

    private async Task<WebViewHostCommand?> RouteDocumentAsync(
        JsonElement payload,
        nint ownerHandle,
        CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var type = typeElement.GetString();
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (type is "transcript-applied" or "transcript-rejected" or "transcript-resync")
        {
            if (type == "transcript-resync") _transcriptDelivery.Resynchronize();
            else if (payload.TryGetProperty("revision", out var value) && value.TryGetInt64(out var revision))
            {
                if (type == "transcript-applied") _transcriptDelivery.Acknowledge(revision);
                else _transcriptDelivery.Reject(revision);
            }
            return null;
        }

        if (type.StartsWith("activity-panel/", StringComparison.Ordinal) && _activityPanelBridge is not null)
        {
            await _activityPanelBridge.HandleAsync(type, payload, cancellationToken);
            return null;
        }

        if (type.StartsWith("appearance/", StringComparison.Ordinal))
        {
            var appearance = await _appearanceSettingsBridge.TryHandleAsync(type, payload, cancellationToken);
            if (appearance is null) throw new ArgumentException("Unsupported appearance message.");
            _hostChannel.PostResponse(appearance.Value.Response);
            return appearance.Value.IsDark is { } dark
                ? new WebViewHostCommand(WebViewHostCommandKind.ApplyCaptionTheme, dark ? "dark" : "light") : null;
        }
        if (type.StartsWith("terminal-", StringComparison.Ordinal))
        {
            if (!await _terminalHostController.TryHandleMessageAsync(type, payload))
                throw new ArgumentException("Unsupported terminal message.");
            return null;
        }
        object? response;
        switch (type)
        {
            case var name when name.StartsWith("ai-providers/", StringComparison.Ordinal):
                response = await _aiProviderSettingsBridge.TryHandleAsync(type, payload, cancellationToken); break;
            case var name when name.StartsWith("agents/", StringComparison.Ordinal):
                response = await _agentSettingsBridge.TryHandleAsync(type, payload, cancellationToken); break;
            case var name when name.StartsWith("extensions/", StringComparison.Ordinal):
                response = await _extensionSettingsBridge.TryHandleAsync(type, payload, _viewModel.SelectedAgentId, cancellationToken); break;
            case "plugin-host/api":
                response = await _pluginPanelBridge.TryHandleAsync(type, payload, cancellationToken); break;
            case var name when name.StartsWith("plugin-host/", StringComparison.Ordinal):
                response = await _pluginPanelHostController.TryHandleAsync(type, payload, cancellationToken); break;
            case "get-git-state":
            case var name when name.StartsWith("git-", StringComparison.Ordinal):
                response = _gitWorkspaceBridge is null ? null : await _gitWorkspaceBridge.TryHandleAsync(type, payload, cancellationToken); break;
            case "scan-programming-clis" or "get-programming-assistant-settings" or "select-programming-cli" or
                 "select-programming-model" or "select-programming-reasoning" or "test-programming-cli":
                response = await _programmingAssistantSettingsBridge.TryHandleAsync(type, payload, cancellationToken); break;
            case "get-pet-settings" or "set-pet-visible" or "select-builtin-pet":
                response = await _petSettingsBridge.TryHandleAsync(type, payload, cancellationToken); break;
            case "get-workspace-selection" or "select-workspace-root" or "browse-workspace-folder" or
                 "delete-workspace-root" or "workspace-tree/list":
                response = await _workspaceSelectionBridge.TryHandleAsync(type, payload, ownerHandle, cancellationToken); break;
            default:
                var command = await RouteShellIntentAsync(type, payload);
                if (type is not ("send-prompt" or "tool-approval/get-state") && ReadOptionalString(payload, "requestId") is { } requestId)
                    _hostChannel.PostResponse(new { type = "operation-result", requestId, ok = true });
                return command;
        }
        if (response is null) throw new ArgumentException($"Unsupported message '{type}'.");
        _hostChannel.PostResponse(response);
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        lock (_lifecycleGate) _stopping = true;
    }

    private async Task<WebViewHostCommand?> RouteShellIntentAsync(string type, JsonElement payload)
    {
        switch (type)
        {
            case "send-prompt":
            {
                var result = await _viewModel.SubmitPromptAsync(
                    ReadOptionalString(payload, "prompt") ?? string.Empty,
                    ReadOptionalString(payload, "workspaceMode"),
                    Guid.TryParse(ReadOptionalString(payload, "modelProfileId"), out var modelProfileId) ? modelProfileId : null);
                var requestId = ReadOptionalString(payload, "requestId");
                if (requestId is not null)
                {
                    _hostChannel.PostResponse(new
                    {
                        type = "prompt-submission",
                        requestId,
                        result.Accepted,
                        result.Error
                    });
                }

                return null;
            }
            case "stop-generation":
                _viewModel.StopSelectedConversation();
                return null;
            case "tool-approval/get-state":
                _hostChannel.PostResponse(_approvals.Capture(ReadOptionalString(payload, "requestId")));
                return null;
            case "resolve-tool-approval":
                ResolveToolApproval(payload);
                return null;
            case "new-chat":
                await _viewModel.StartNewConversationAsync();
                return null;
            case "select-conversation":
                await SelectConversationAsync(payload);
                return null;
            case "delete-conversation":
                await DeleteConversationAsync(payload);
                return null;
            case "clear-conversations":
                await DeleteConversationsAsync(payload);
                return null;
            case "select-composer-mode":
                await _viewModel.SelectComposerModeAsync(ReadOptionalString(payload, "mode"));
                return null;
            case "select-agent":
                await _viewModel.SelectAgentAsync(ReadOptionalString(payload, "agentId"));
                return null;
            case "select-tool-permission-mode":
                await _viewModel.SelectToolPermissionModeAsync(ReadOptionalString(payload, "mode"));
                return null;
            case "open-link":
                return new WebViewHostCommand(WebViewHostCommandKind.OpenLink, ReadOptionalString(payload, "href"));
            case "window-drag":
                return new WebViewHostCommand(WebViewHostCommandKind.StartWindowDrag);
            // 缩放热区在网页四周（WebView2 铺满整个窗口，父窗口拿不到边缘的鼠标消息）。
            // edge 是方位名，宿主侧再映射到 HT* 码；这里不校验，交给宿主的白名单。
            case "window-resize":
                return new WebViewHostCommand(
                    WebViewHostCommandKind.StartWindowResize,
                    ReadOptionalString(payload, "edge"));
            case "window-minimize":
                return new WebViewHostCommand(WebViewHostCommandKind.MinimizeWindow);
            case "window-toggle-maximize":
                return new WebViewHostCommand(WebViewHostCommandKind.ToggleMaximizeWindow);
            case "window-close":
                return new WebViewHostCommand(WebViewHostCommandKind.CloseWindow);
            case "toggle-terminal":
                return new WebViewHostCommand(WebViewHostCommandKind.ToggleTerminal);
            default:
                if (ReadOptionalString(payload, "requestId") is null) return null;
                throw new ArgumentException($"Unsupported message '{type}'.");
        }
    }

    private void ResolveToolApproval(JsonElement payload)
    {
        var id = ReadRequiredGuid(payload, "toolExecutionId");
        if (!payload.TryGetProperty("approved", out var approval) || approval.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("A boolean approval decision is required.");
        if (!_approvals.Resolve(id, approval.GetBoolean()))
            throw new InvalidOperationException("This approval has already completed or expired.");
    }

    private Task SelectConversationAsync(JsonElement payload)
        => _viewModel.SelectConversationAsync(ReadRequiredGuid(payload, "conversationId"));

    private Task DeleteConversationAsync(JsonElement payload)
        => _viewModel.DeleteConversationAsync(ReadRequiredGuid(payload, "conversationId"), ReadBoolean(payload, "removeManagedWorktree"));

    private Task DeleteConversationsAsync(JsonElement payload)
    {
        if (!payload.TryGetProperty("conversationIds", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("conversationIds must be an array.");
        var ids = values.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String &&
            Guid.TryParse(item.GetString(), out var id) && id != Guid.Empty ? id :
            throw new ArgumentException("Every conversation id must be a non-empty GUID.")).ToArray();
        return _viewModel.DeleteConversationsAsync(ids);
    }

    private static Guid ReadRequiredGuid(JsonElement payload, string property)
        => Guid.TryParse(ReadOptionalString(payload, property), out var value) && value != Guid.Empty ? value :
            throw new ArgumentException($"{property} must be a non-empty GUID.");

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool ReadBoolean(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var element) &&
           element.ValueKind is JsonValueKind.True or JsonValueKind.False &&
           element.GetBoolean();
}
