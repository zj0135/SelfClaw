using System.Text.Json;
using System.Windows.Threading;
using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Desktop.Services.Extensions;
using SelfClaw.Desktop.Services.Git;
using SelfClaw.Desktop.Services.Pet;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.ViewModels;

namespace SelfClaw.Desktop.Services.WebView;

internal sealed class WebViewMessageRouter : IDisposable
{
    private readonly AiProviderSettingsBridge _aiProviderSettingsBridge;
    private readonly ExtensionSettingsBridge _extensionSettingsBridge;
    private readonly AgentSettingsBridge _agentSettingsBridge;
    private readonly IExtensionStateChangeNotifier _extensionStateChangeNotifier;
    private readonly ProgrammingAssistantSettingsBridge _programmingAssistantSettingsBridge;
    private readonly PetSettingsBridge _petSettingsBridge;
    private readonly WorkspaceSelectionBridge _workspaceSelectionBridge;
    private readonly GitWorkspaceBridge? _gitWorkspaceBridge;
    private readonly TerminalHostController _terminalHostController;
    private readonly MainWindowViewModel _viewModel;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly WebViewHostChannel _hostChannel;
    private readonly Dispatcher _dispatcher;
    private int _disposeStarted;

    public WebViewMessageRouter(
        AiProviderSettingsBridge aiProviderSettingsBridge,
        ExtensionSettingsBridge extensionSettingsBridge,
        AgentSettingsBridge agentSettingsBridge,
        IExtensionStateChangeNotifier extensionStateChangeNotifier,
        ProgrammingAssistantSettingsBridge programmingAssistantSettingsBridge,
        PetSettingsBridge petSettingsBridge,
        WorkspaceSelectionBridge workspaceSelectionBridge,
        TerminalHostController terminalHostController,
        MainWindowViewModel viewModel,
        AgentActivityCoordinator agentActivityCoordinator,
        WebViewHostChannel hostChannel,
        Dispatcher dispatcher,
        GitWorkspaceBridge? gitWorkspaceBridge = null)
    {
        _aiProviderSettingsBridge = aiProviderSettingsBridge;
        _extensionSettingsBridge = extensionSettingsBridge;
        _agentSettingsBridge = agentSettingsBridge;
        _extensionStateChangeNotifier = extensionStateChangeNotifier;
        _programmingAssistantSettingsBridge = programmingAssistantSettingsBridge;
        _petSettingsBridge = petSettingsBridge;
        _workspaceSelectionBridge = workspaceSelectionBridge;
        _gitWorkspaceBridge = gitWorkspaceBridge;
        _terminalHostController = terminalHostController;
        _viewModel = viewModel;
        _agentActivityCoordinator = agentActivityCoordinator;
        _hostChannel = hostChannel;
        _dispatcher = dispatcher;

        _aiProviderSettingsBridge.ModelSelectionChanged += OnModelSelectionChanged;
        _agentSettingsBridge.AgentsChanged += OnAgentsChanged;
        _extensionStateChangeNotifier.StateChanged += OnExtensionStateChanged;
    }

    public async Task<WebViewHostCommand?> RouteAsync(
        string messageJson,
        nint ownerHandle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
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
            return await RouteDocumentAsync(document.RootElement, ownerHandle, cancellationToken);
        }
    }

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

        if (string.Equals(type, "transcript-rendered", StringComparison.Ordinal))
        {
            AcknowledgeTranscript(payload);
            return null;
        }

        var response = await _aiProviderSettingsBridge.TryHandleAsync(
            type,
            payload,
            cancellationToken);
        if (response is not null)
        {
            _hostChannel.PostResponse(response);
            return null;
        }

        if (_gitWorkspaceBridge is not null)
        {
            response = await _gitWorkspaceBridge.TryHandleAsync(type, payload, cancellationToken);
            if (response is not null)
            {
                _hostChannel.PostResponse(response);
                return null;
            }
        }

        response = await _extensionSettingsBridge.TryHandleAsync(
            type,
            payload,
            _viewModel.SelectedAgentId,
            cancellationToken);
        if (response is not null)
        {
            _hostChannel.PostResponse(response);
            return null;
        }

        response = await _agentSettingsBridge.TryHandleAsync(
            type,
            payload,
            cancellationToken);
        if (response is not null)
        {
            _hostChannel.PostResponse(response);
            return null;
        }

        response = await _programmingAssistantSettingsBridge.TryHandleAsync(
            type,
            payload,
            cancellationToken);
        if (response is not null)
        {
            _hostChannel.PostResponse(response);
            return null;
        }

        response = await _petSettingsBridge.TryHandleAsync(
            type,
            payload,
            cancellationToken);
        if (response is not null)
        {
            _hostChannel.PostResponse(response);
            return null;
        }

        response = await _workspaceSelectionBridge.TryHandleAsync(
            type,
            payload,
            ownerHandle,
            cancellationToken);
        if (response is not null)
        {
            _hostChannel.PostResponse(response);
            return null;
        }

        if (_terminalHostController.TryHandleMessage(type, payload))
        {
            return null;
        }

        return await RouteShellIntentAsync(type, payload);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _aiProviderSettingsBridge.ModelSelectionChanged -= OnModelSelectionChanged;
        _agentSettingsBridge.AgentsChanged -= OnAgentsChanged;
        _extensionStateChangeNotifier.StateChanged -= OnExtensionStateChanged;
    }

    private async Task<WebViewHostCommand?> RouteShellIntentAsync(string type, JsonElement payload)
    {
        switch (type)
        {
            case "send-prompt":
            {
                var result = await _viewModel.SubmitPromptAsync(
                    ReadOptionalString(payload, "prompt") ?? string.Empty,
                    ReadOptionalString(payload, "workspaceMode"));
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
            case "delete-workspace-root":
                await DeleteWorkspaceRootAsync(payload);
                return null;
            case "select-composer-mode":
                await _viewModel.SelectComposerModeAsync(ReadOptionalString(payload, "mode"));
                return null;
            case "select-tool-permission-mode":
                await _viewModel.SelectToolPermissionModeAsync(ReadOptionalString(payload, "mode"));
                return null;
            case "open-link":
                return new WebViewHostCommand(WebViewHostCommandKind.OpenLink, ReadOptionalString(payload, "href"));
            case "window-drag":
                return new WebViewHostCommand(WebViewHostCommandKind.StartWindowDrag);
            case "window-minimize":
                return new WebViewHostCommand(WebViewHostCommandKind.MinimizeWindow);
            case "window-toggle-maximize":
                return new WebViewHostCommand(WebViewHostCommandKind.ToggleMaximizeWindow);
            case "window-close":
                return new WebViewHostCommand(WebViewHostCommandKind.CloseWindow);
            case "toggle-terminal":
                return new WebViewHostCommand(WebViewHostCommandKind.ToggleTerminal);
            case "toggle-files":
                return new WebViewHostCommand(WebViewHostCommandKind.ToggleFiles);
            case "toggle-browser":
                return new WebViewHostCommand(WebViewHostCommandKind.ToggleBrowser);
            case "settings-closed":
                return new WebViewHostCommand(WebViewHostCommandKind.SettingsClosed);
            default:
                return null;
        }
    }

    private void AcknowledgeTranscript(JsonElement payload)
    {
        if (payload.TryGetProperty("revision", out var revisionElement) &&
            revisionElement.TryGetInt64(out var revision))
        {
            _hostChannel.AcknowledgeTranscript(revision);
        }
    }

    private void ResolveToolApproval(JsonElement payload)
    {
        var toolExecutionId = ReadOptionalString(payload, "toolExecutionId");
        var approved = payload.TryGetProperty("approved", out var approvedElement) &&
                       approvedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                       approvedElement.GetBoolean();
        if (Guid.TryParse(toolExecutionId, out var parsedToolExecutionId))
        {
            _agentActivityCoordinator.TryResolveApproval(parsedToolExecutionId, approved);
        }
    }

    private async Task SelectConversationAsync(JsonElement payload)
    {
        if (Guid.TryParse(ReadOptionalString(payload, "conversationId"), out var conversationId))
        {
            await _viewModel.SelectConversationAsync(conversationId);
        }
    }

    private async Task DeleteConversationAsync(JsonElement payload)
    {
        if (Guid.TryParse(ReadOptionalString(payload, "conversationId"), out var conversationId))
        {
            await _viewModel.DeleteConversationAsync(
                conversationId,
                ReadBoolean(payload, "removeManagedWorktree"));
        }
    }

    private async Task DeleteConversationsAsync(JsonElement payload)
    {
        if (!payload.TryGetProperty("conversationIds", out var conversationIdsElement) ||
            conversationIdsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var conversationIds = conversationIdsElement
            .EnumerateArray()
            .Select(item => Guid.TryParse(item.GetString(), out var conversationId) ? conversationId : (Guid?)null)
            .Where(conversationId => conversationId.HasValue)
            .Select(conversationId => conversationId.GetValueOrDefault())
            .ToArray();
        await _viewModel.DeleteConversationsAsync(conversationIds);
    }

    private async Task DeleteWorkspaceRootAsync(JsonElement payload)
    {
        if (Guid.TryParse(ReadOptionalString(payload, "workspaceRootId"), out var workspaceRootId))
        {
            await _viewModel.DeleteWorkspaceRootAsync(workspaceRootId);
        }
    }

    private void OnModelSelectionChanged(Guid? modelProfileId)
        => RunOnDispatcher(() => _viewModel.SelectModelProfile(modelProfileId));

    // Agent 定义文件已落盘：先刷新 VM 的 Agent 缓存，随后的 revision 推进会带着新定义重绘 transcript。
    private void OnAgentsChanged()
        => RunOnDispatcher(() => _viewModel.ReloadAgents());

    private void OnExtensionStateChanged(long revision)
        => RunOnDispatcher(() =>
        {
            _viewModel.UpdateCapabilityRevision(revision);
            _hostChannel.PostPush(new { type = "extensions/state-changed", revision });
        });

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

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
