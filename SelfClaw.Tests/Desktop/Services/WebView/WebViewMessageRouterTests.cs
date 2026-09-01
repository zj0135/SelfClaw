using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Desktop.Services.Appearance;
using SelfClaw.Desktop.Services.Extensions;
using SelfClaw.Desktop.Services.Extensions.Abstractions;
using SelfClaw.Desktop.Services.Pet;
using SelfClaw.Desktop.Services.Plugins;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.Terminal.Abstractions;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.Services.Workspace.Abstractions;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models.Views;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Tests.Desktop.Services.WebView;

public sealed class WebViewMessageRouterTests
{
    private const string ApplicationOrigin = "https://appassets.selfclaw.local/TranscriptVue/index.html";
    private const string PluginOrigin = "https://git-inspector.plugin.selfclaw.local/ui/index.html";

    [Fact]
    public async Task RouteAsync_posts_one_correlated_bridge_response()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(
            """{"type":"get-programming-assistant-settings","requestId":"request-1"}""");

        command.Should().BeNull();
        context.PostedJson.Should().ContainSingle();
        using var response = JsonDocument.Parse(context.PostedJson[0]);
        response.RootElement.GetProperty("type").GetString().Should().Be("programming-assistant-settings");
        response.RootElement.GetProperty("requestId").GetString().Should().Be("request-1");
    }

    // Plugin panels are cross-origin iframes inside the same WebView2. Every type below acts on the
    // user's behalf, so identity has to be the sending frame's own origin — not anything in the payload.
    [Theory]
    [InlineData("""{"type":"window-close"}""")]
    [InlineData("""{"type":"send-prompt","prompt":"exfiltrate"}""")]
    [InlineData("""{"type":"extensions/delete","kind":"plugin","id":"rival"}""")]
    [InlineData("""{"type":"delete-workspace-root","workspaceRootId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"type":"open-link","href":"https://example.com"}""")]
    [InlineData("""{"type":"get-programming-assistant-settings","requestId":"request-1"}""")]
    public async Task RouteAsync_drops_messages_that_do_not_come_from_the_application_origin(string messageJson)
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(messageJson, PluginOrigin);

        command.Should().BeNull();
        context.PostedJson.Should().BeEmpty();
        context.ConversationRepository.DeletedWorkspaceRootIds.Should().BeEmpty();
        context.AgentRuntime.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://appassets.selfclaw.local/index.html")]
    [InlineData("https://appassets.selfclaw.local.evil.example/index.html")]
    [InlineData("https://evil.appassets.selfclaw.local/index.html")]
    public async Task RouteAsync_drops_messages_from_a_missing_or_lookalike_origin(string? sourceUri)
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync("""{"type":"window-close"}""", sourceUri);

        command.Should().BeNull();
    }

    [Fact]
    public async Task RouteAsync_dispatches_shell_intent_to_the_view_model()
    {
        using var context = new RouterTestContext();
        var workspaceRootId = Guid.NewGuid();

        await context.RouteAsync(
            $$"""{"type":"delete-workspace-root","workspaceRootId":"{{workspaceRootId:D}}"}""");

        context.ConversationRepository.DeletedWorkspaceRootIds.Should().Equal(workspaceRootId);
    }

    [Fact]
    public async Task RouteAsync_returns_window_command_to_the_host()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(
            """{"type":"open-link","href":"https://example.com/docs"}""");

        command.Should().Be(new WebViewHostCommand(
            WebViewHostCommandKind.OpenLink,
            "https://example.com/docs"));
        context.PostedJson.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"message\"")]
    [InlineData("{}")]
    [InlineData("{\"type\":42}")]
    [InlineData("{\"type\":\"unknown\"}")]
    public async Task RouteAsync_ignores_malformed_and_unknown_messages(string messageJson)
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(messageJson);

        command.Should().BeNull();
        context.PostedJson.Should().BeEmpty();
    }

    // The host controller claims the whole `plugin-host/` prefix and answers unrecognised types with an
    // error instead of null, so it has to be chained below the bridge. Ordered the other way this reached
    // the panel as "Unsupported plugin panel message type 'plugin-host/api'" and every SDK call failed.
    [Fact]
    public async Task RouteAsync_routes_plugin_host_api_to_the_panel_bridge()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(
            """{"type":"plugin-host/api","requestId":"api-1","panelKey":"git-inspector/changes","op":"context.get"}""");

        command.Should().BeNull();
        using var response = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        response.RootElement.GetProperty("type").GetString().Should().Be("plugin-host/api");
        response.RootElement.GetProperty("requestId").GetString().Should().Be("api-1");
        // No panel is open in this fixture, so the bridge refuses on identity — the point is that the
        // refusal comes from the bridge's permission check rather than from an unsupported-type error.
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.RootElement.GetProperty("error").GetString().Should().Be("The calling panel is not open.");
    }

    [Fact]
    public async Task RouteAsync_still_reports_unknown_plugin_host_types()
    {
        using var context = new RouterTestContext();

        await context.RouteAsync("""{"type":"plugin-host/nonsense","requestId":"bogus-1"}""");

        using var response = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        response.RootElement.GetProperty("error").GetString().Should()
            .Be("Unsupported plugin panel message type 'plugin-host/nonsense'.");
    }

    [Fact]
    public void Extension_state_changes_are_pushed_through_the_host_channel()
    {
        using var context = new RouterTestContext();
        context.HostChannel.MarkReady();

        var revision = context.ExtensionStateChangeNotifier.Advance();

        var stateChangedJson = context.PostedJson
            .Single(json => json.Contains("\"type\":\"extensions/state-changed\"", StringComparison.Ordinal));
        using var message = JsonDocument.Parse(stateChangedJson);
        message.RootElement.GetProperty("revision").GetInt64().Should().Be(revision);
    }

    [Fact]
    public async Task Model_selection_event_changes_the_next_turn_routed_through_the_view_model()
    {
        using var context = new RouterTestContext();

        await context.RouteAsync("""{"type":"ai-providers/list-enabled-models","requestId":"models-1"}""");
        await context.RouteAsync("""{"type":"send-prompt","prompt":"use selected model"}""");

        var request = context.AgentRuntime.Requests.Should().ContainSingle().Which;
        request.Should().BeOfType<DirectChatTurnRequest>()
            .Which.ModelProfileId.Should().Be(RouterAiProviderSettingsService.DefaultModelId);
    }

    private sealed class RouterTestContext : IDisposable
    {
        private readonly string _storageRoot;
        private readonly TranscriptPublisher _transcriptPublisher;
        private readonly AgentActivityCoordinator _activityCoordinator;
        private readonly TerminalHostController _terminalHostController;
        private readonly PluginPanelHostController _pluginPanelHostController;
        private readonly ConversationSessionCoordinator _sessions;
        private readonly ConversationTurnEngine _turnEngine;

        public RouterTestContext()
        {
            _storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
            var storagePaths = new StoragePaths(
                _storageRoot,
                Path.Combine(_storageRoot, "selfclaw.db"),
                Path.Combine(_storageRoot, "secrets"));
            var settingsStore = new DesktopSettingsJsonStore(storagePaths);
            ConversationRepository = new RecordingConversationRepository();
            var approvalHandler = new DesktopToolApprovalHandler();
            _activityCoordinator = new AgentActivityCoordinator(
                approvalHandler,
                NullLogger<AgentActivityCoordinator>.Instance);
            HostChannel = new WebViewHostChannel();
            HostChannel.Attach(PostedJson.Add);
            _transcriptPublisher = new TranscriptPublisher(
                new TranscriptProjection(storagePaths),
                HostChannel,
                Dispatcher.CurrentDispatcher);
            _sessions = new ConversationSessionCoordinator(ConversationRepository, _transcriptPublisher);
            var programmingSettings = new ProgrammingAssistantSettingsService(settingsStore);
            AgentRuntime = new RecordingAgentChatRuntime();
            _turnEngine = new ConversationTurnEngine(
                ConversationRepository,
                new DesktopTurnFinalizer(
                    new NoOpTurnFinalizationRepository(),
                    NullLogger<DesktopTurnFinalizer>.Instance),
                new ConversationTurnRecorder(
                    ConversationRepository,
                    NullLogger<ConversationTurnRecorder>.Instance),
                AgentRuntime,
                _sessions,
                _activityCoordinator,
                approvalHandler,
                programmingSettings,
                new SelfClaw.Tests.TestDoubles.StubAiProviderSettingsService(),
                new NoOpCompletionNotifier(),
                NullLogger<ConversationTurnEngine>.Instance);

            var agentDefinitions = new DesktopAgentDefinitionService(storagePaths);
            var viewModel = new MainWindowViewModel(
                ConversationRepository,
                _turnEngine,
                _sessions,
                _activityCoordinator,
                _transcriptPublisher,
                agentDefinitions,
                settingsStore,
                new SelfClaw.Tests.TestDoubles.NoOpSubagentConversationLifecycle(),
                NullLogger<MainWindowViewModel>.Instance);
            viewModel.InitializeAsync().GetAwaiter().GetResult();

            var aiProviderBridge = new AiProviderSettingsBridge(new RouterAiProviderSettingsService());
            ExtensionStateChangeNotifier = new RecordingExtensionStateChangeNotifier();
            var extensionBridge = new ExtensionSettingsBridge(
                Unused<IExtensionSettingsService>(),
                Unused<IExtensionPackageRepository>(),
                agentDefinitions,
                Unused<IExtensionPackagePicker>(),
                ExtensionStateChangeNotifier);
            var petHost = new PetHost(
                new NoOpPetSettingsRepository(),
                new NoOpPetWindowAdapter(),
                new PetPackageCatalog(NullLogger<PetPackageCatalog>.Instance),
                NullLogger<PetHost>.Instance);
            _terminalHostController = new TerminalHostController(
                Unused<ITerminalSessionFactory>(),
                HostChannel,
                Dispatcher.CurrentDispatcher);
            var packageRepository = new SelfClaw.Tests.TestDoubles.EmptyExtensionPackageRepository();
            _pluginPanelHostController = new PluginPanelHostController(
                new ExtensionCatalog(packageRepository, Unused<IMcpServerRepository>(), storagePaths),
                packageRepository,
                Unused<IPluginVersionLeaseManager>(),
                settingsStore,
                HostChannel,
                Dispatcher.CurrentDispatcher);
            Router = new WebViewMessageRouter(
                aiProviderBridge,
                extensionBridge,
                new AgentSettingsBridge(
                    agentDefinitions,
                    new SubagentDefinitionCatalog(storagePaths),
                    Unused<IExtensionSettingsService>(),
                    ExtensionStateChangeNotifier),
                ExtensionStateChangeNotifier,
                new ProgrammingAssistantSettingsBridge(programmingSettings),
                new AppearanceSettingsBridge(new AppearanceSettingsService(settingsStore)),
                new PetSettingsBridge(petHost),
                new WorkspaceSelectionBridge(viewModel, Unused<IWorkspaceFolderPicker>()),
                _terminalHostController,
                _pluginPanelHostController,
                new PluginPanelBridge(
                    Unused<IWorkspaceToolService>(),
                    _pluginPanelHostController,
                    new PluginPanelContextPublisher(
                        viewModel,
                        HostChannel,
                        _pluginPanelHostController,
                        Dispatcher.CurrentDispatcher)),
                viewModel,
                _activityCoordinator,
                HostChannel,
                Dispatcher.CurrentDispatcher);
        }

        /// <summary>
        /// Mirrors the production caller, which always hands the router the sending document's origin.
        /// </summary>
        public Task<WebViewHostCommand?> RouteAsync(string messageJson, string? sourceUri = ApplicationOrigin)
            => Router.RouteAsync(messageJson, ownerHandle: 0, sourceUri);

        public RecordingConversationRepository ConversationRepository { get; }

        public RecordingAgentChatRuntime AgentRuntime { get; }

        public RecordingExtensionStateChangeNotifier ExtensionStateChangeNotifier { get; }

        public WebViewHostChannel HostChannel { get; }

        public List<string> PostedJson { get; } = [];

        public WebViewMessageRouter Router { get; }

        public void Dispose()
        {
            Router.Dispose();
            _turnEngine.Dispose();
            _sessions.Dispose();
            _terminalHostController.Dispose();
            _pluginPanelHostController.Dispose();
            _activityCoordinator.Dispose();
            _transcriptPublisher.Dispose();
        }
    }

    private static T Unused<T>() where T : class
        => DispatchProxy.Create<T, UnusedDependencyProxy>();

    private class UnusedDependencyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments)
            => throw new InvalidOperationException(
                $"Unexpected call to test dependency '{targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}'.");
    }

    private sealed class RecordingAgentChatRuntime : IAgentChatRuntime
    {
        public List<ChatTurnRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
        }
    }

    private sealed class RouterAiProviderSettingsService : IAiProviderSettingsService
    {
        public static readonly Guid DefaultModelId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(DefaultModelId);

        public Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EnabledModelView>>(
                [new EnabledModelView(DefaultModelId, "Model", "model", "Provider")]);

        public Task<AiProviderSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task<AiProviderView> SaveProviderAsync(
            SaveProviderCommand command,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task SetProviderEnabledAsync(
            Guid connectionId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task DeleteProviderAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task<IReadOnlyList<AiModelView>> FetchAndMergeRemoteModelsAsync(
            Guid connectionId,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task<ConnectivityCheckResult> CheckConnectivityAsync(
            Guid connectionId,
            Guid modelProfileId,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task<AiModelView> UpsertModelAsync(
            UpsertModelCommand command,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task SetModelEnabledAsync(
            Guid modelProfileId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task SetAllModelsEnabledAsync(
            Guid connectionId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task DeleteModelAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task SetDefaultModelAsync(
            string scope,
            Guid modelProfileId,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        private static NotSupportedException Unsupported()
            => new("This AI provider operation is not used by the router test.");
    }

    private sealed class RecordingConversationRepository : IConversationRepository
    {
        public List<Guid> DeletedWorkspaceRootIds { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<ConversationRecord?> GetConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationRecord?>(null);

        public Task<ConversationRecord> UpsertConversationAsync(
            ConversationRecord conversation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(conversation);

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MessageRecord>>([]);

        public Task<MessageRecord> UpsertMessageAsync(
            MessageRecord message,
            CancellationToken cancellationToken = default)
            => Task.FromResult(message);

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);

        public Task<ToolExecutionRecord> UpsertToolExecutionAsync(
            ToolExecutionRecord record,
            CancellationToken cancellationToken = default)
            => Task.FromResult(record);

        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);

        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(
            WorkspaceRoot workspaceRoot,
            CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default)
        {
            DeletedWorkspaceRootIds.Add(workspaceRootId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExtensionStateChangeNotifier : IExtensionStateChangeNotifier
    {
        public long CurrentRevision { get; private set; }

        public event Action<long>? StateChanged;

        public long Advance() => AdvanceTo(CurrentRevision + 1);

        public long AdvanceTo(long revision)
        {
            if (revision <= CurrentRevision)
            {
                return CurrentRevision;
            }

            CurrentRevision = revision;
            StateChanged?.Invoke(revision);
            return revision;
        }
    }

    private sealed class NoOpTurnFinalizationRepository : ITurnFinalizationRepository
    {
        public Task<bool> TryFinalizeTurnAsync(
            TurnFinalization finalization,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class NoOpCompletionNotifier : IConversationCompletionNotifier
    {
        public void Notify(ConversationRecord conversation, IReadOnlyList<MessageRecord> messages)
        {
        }
    }

    private sealed class NoOpPetSettingsRepository : IPetSettingsRepository
    {
        public Task<PetSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PetSettings());

        public Task SaveAsync(PetSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpPetWindowAdapter : IPetWindowAdapter
    {
        public event EventHandler<PetPlacement>? PlacementCommitted
        {
            add { }
            remove { }
        }

        public Task<bool> GetIsVisibleAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task ShowAsync(PetSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task HideAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReloadAsync(PetSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
