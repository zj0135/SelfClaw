using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Agents.Definitions;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.ViewModels;

public sealed class ShellStateTests
{
    [Fact]
    public Task Concurrent_composer_mode_saves_leave_disk_and_display_at_the_same_committed_value()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new ShellContext();
            await context.ViewModel.InitializeAsync();
            await Task.WhenAll(context.ViewModel.SelectComposerModeAsync("cli"), context.ViewModel.SelectComposerModeAsync("direct"));
            var persisted = await context.Settings.ReadNodeAsync<SelfClaw.Desktop.Services.ProgrammingAssistant.Models.ComposerSettings>("composer");
            persisted?.ExecutionMode.Should().Be("direct");
            ((SelfClaw.Desktop.Services.Plugins.IPluginPanelContextSource)context.ViewModel).CaptureContext().AgentMode.Should().Be("direct");
        });

    [Fact]
    public Task Choosing_another_workspace_deselects_the_old_conversation_without_clearing_the_new_workspace()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new ShellContext();
            var first = await context.AddRootAsync("first");
            var second = await context.AddRootAsync("second");
            var conversation = await context.AddConversationAsync("build", first.Id);
            await context.ViewModel.InitializeAsync();
            await context.ViewModel.SelectConversationAsync(conversation.Id);
            context.ViewModel.SelectWorkspaceRoot(second.Id);
            context.ViewModel.SelectedWorkspaceRoot?.Id.Should().Be(second.Id);
            ((SelfClaw.Desktop.Services.Plugins.IPluginPanelContextSource)context.ViewModel).CaptureContext().ConversationId.Should().BeNull();
        });

    [Fact]
    public Task Agent_refresh_preserves_display_context_and_runtime_and_deleted_definitions_fail_explicitly()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new ShellContext();
            context.Agents.CreateAgent(new("agent-a", "Agent A", "", AgentExecutionMode.Direct, "A instructions"));
            var conversation = await context.AddConversationAsync("agent-a");
            await context.ViewModel.InitializeAsync();
            await context.ViewModel.SelectConversationAsync(conversation.Id);
            context.Agents.CreateAgent(new("agent-b", "Agent B", "", AgentExecutionMode.Cli, "B instructions"));
            context.ViewModel.SelectedAgentId.Should().Be("agent-a");
            ((SelfClaw.Desktop.Services.Plugins.IPluginPanelContextSource)context.ViewModel).CaptureContext().AgentId.Should().Be("agent-a");
            (await context.ViewModel.SubmitPromptAsync("use A")).Accepted.Should().BeTrue();
            var request = await context.Runtime.Requested.Task;
            request.Agent.Id.Should().Be("agent-a");
            await context.StopTurnsAsync();
            context.Agents.DeleteAgent("agent-a");
            context.ViewModel.SelectedAgentId.Should().Be("agent-a");
            var rejected = await context.ViewModel.SubmitPromptAsync("missing definition");
            rejected.Accepted.Should().BeFalse();
            rejected.Error.Should().Contain("agent-a");
        });

    [Fact]
    public Task Notification_arguments_activate_the_owned_interactive_conversation_and_report_deleted_targets()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new ShellContext();
            var conversation = await context.AddConversationAsync("build");
            var activation = new SelfClaw.Desktop.Services.Windowing.DesktopConversationActivationService(context.ViewModel,
                context.Conversations, new WebViewHostChannel(), Dispatcher.CurrentDispatcher,
                NullLogger<SelfClaw.Desktop.Services.Windowing.DesktopConversationActivationService>.Instance);
            var notifications = new DesktopNotificationActivationService(activation, new DesktopToolApprovalHandler(),
                NullLogger<DesktopNotificationActivationService>.Instance);
            var arguments = DesktopNotificationArguments.Build((DesktopNotificationArguments.ActionKey, DesktopNotificationArguments.OpenConversationAction),
                (DesktopNotificationArguments.ConversationIdKey, conversation.Id.ToString("D")));
            (await notifications.HandleActivationAsync(arguments)).Should().BeTrue();
            ((SelfClaw.Desktop.Services.Plugins.IPluginPanelContextSource)context.ViewModel).CaptureContext().ConversationId.Should().Be(conversation.Id.ToString("D"));
            await context.Conversations.DeleteConversationAsync(conversation.Id);
            (await notifications.HandleActivationAsync(arguments)).Should().BeFalse();
        });

    [Fact]
    public Task Initialization_is_shared_and_workspace_add_failure_can_be_retried() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var context = new ShellContext();
        await context.AddRootAsync("first");
        var first = context.ViewModel.InitializeAsync();
        var second = context.ViewModel.InitializeAsync();
        second.Should().BeSameAs(first);
        await first;
        context.ViewModel.WorkspaceRoots.Should().ContainSingle();

        var added = Path.Combine(context.RootPath, "added");
        Directory.CreateDirectory(added);
        context.Query.PauseNext();
        var add = context.ViewModel.SelectOrAddWorkspaceRootAsync(added);
        await context.Query.Entered.Task;
        context.Query.Release.TrySetException(new IOException("delayed read failed"));
        await FluentActions.Awaiting(() => add).Should().ThrowAsync<IOException>();
        await context.ViewModel.SelectOrAddWorkspaceRootAsync(added);
        context.ViewModel.WorkspaceRoots.Should().HaveCount(2);
    });

    [Fact]
    public Task Delayed_workspace_refresh_preserves_new_selection_and_notifies_only_on_dispatcher()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new ShellContext();
            var first = await context.AddRootAsync("first");
            var second = await context.AddRootAsync("second");
            await context.ViewModel.InitializeAsync();
            context.ViewModel.SelectWorkspaceRoot(first.Id);
            var notifications = 0;
            var dispatcher = Dispatcher.CurrentDispatcher;
            context.ViewModel.PropertyChanged += (_, _) => { dispatcher.VerifyAccess(); notifications++; };
            context.Query.PauseNext();
            var refresh = context.ViewModel.ReloadWorkspaceSelectionAsync();
            await context.Query.Entered.Task;
            context.ViewModel.SelectWorkspaceRoot(second.Id);
            await Task.Run(() => context.Query.Release.TrySetResult());
            await refresh;
            context.ViewModel.SelectedWorkspaceRoot?.Id.Should().Be(second.Id);
            notifications.Should().BeGreaterThan(0);
        });

    private sealed class ShellContext : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        private readonly SqliteWorkspaceRepository _roots;
        private readonly AgentActivityCoordinator _activity;
        private readonly TranscriptPublisher _publisher;
        private readonly TranscriptDelivery _delivery;
        private readonly ConversationSessionCoordinator _sessions;
        private readonly ConversationTurnEngine _engine;

        public ShellContext()
        {
            var paths = StoragePathDefaults.Create(_root, Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets"));
            var database = new SqliteDatabase(paths);
            var conversations = new SqliteConversationRepository(database);
            Conversations = conversations;
            _roots = new SqliteWorkspaceRepository(database);
            var settings = new DesktopSettingsJsonStore(paths);
            Settings = settings;
            var approval = new DesktopToolApprovalHandler();
            _activity = new AgentActivityCoordinator(approval, NullLogger<AgentActivityCoordinator>.Instance);
            _delivery = new TranscriptDelivery(new WebViewHostChannel(), Dispatcher.CurrentDispatcher);
            _publisher = new TranscriptPublisher(new TranscriptProjection(paths), _delivery, Dispatcher.CurrentDispatcher);
            _sessions = new ConversationSessionCoordinator(conversations, _publisher);
            _engine = new ConversationTurnEngine(conversations, new DesktopTurnFinalizer(conversations, NullLogger<DesktopTurnFinalizer>.Instance),
                new ConversationTurnRecorder(conversations, NullLogger<ConversationTurnRecorder>.Instance), Runtime,
                _sessions, _activity, approval, SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(settings), new SilentCompletion(),
                NullLogger<ConversationTurnEngine>.Instance);
            var workspaces = new ConversationWorkspaceService(_roots, query: Query);
            var changes = new SelfClaw.Infrastructure.Extensions.ExtensionStateChangeNotifier();
            Agents = new AgentSettingsService(new DesktopAgentDefinitionService(paths),
                    new SelfClaw.Desktop.Services.Agents.Definitions.SubagentDefinitionCatalog(paths),
                    new EmptyExtensionSettingsService(), changes);
            ViewModel = new MainWindowViewModel(conversations, _engine, _sessions, _activity, _publisher,
                Agents, changes, settings, workspaces,
                new ConversationDeletionService(_engine, _sessions, new NoOpSubagentConversationLifecycle(), workspaces, conversations),
                NullLogger<MainWindowViewModel>.Instance, Dispatcher.CurrentDispatcher);
        }

        public DelayedQuery Query { get; } = new();
        public string RootPath => _root;
        public MainWindowViewModel ViewModel { get; }
        public DesktopSettingsJsonStore Settings { get; }
        public AgentSettingsService Agents { get; }
        public SqliteConversationRepository Conversations { get; }
        public EmptyRuntime Runtime { get; } = new();
        public Task StopTurnsAsync() => _sessions.StopAsync(CancellationToken.None);
        public Task<ConversationRecord> AddConversationAsync(string agentId, Guid? workspaceRootId = null)
        {
            var now = DateTimeOffset.UtcNow;
            return Conversations.UpsertConversationAsync(new ConversationRecord(Guid.NewGuid(), "Target conversation", workspaceRootId,
                ToolPermissionMode.RequireApproval, agentId, now, now));
        }

        public Task<WorkspaceRoot> AddRootAsync(string name)
        {
            var now = DateTimeOffset.UtcNow;
            return _roots.UpsertWorkspaceRootAsync(new WorkspaceRoot(Guid.NewGuid(), name, Path.Combine(_root, name), now, now));
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _sessions.Dispose();
            _engine.Dispose();
            _publisher.Dispose();
            _delivery.Dispose();
            _activity.Dispose();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "test.db")}");
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class DelayedQuery : IGitWorkspaceQuery
    {
        private bool _pause;
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void PauseNext() { _pause = true; Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<GitWorkspaceState> GetStateAsync(WorkspaceRoot root, CancellationToken cancellationToken = default)
        {
            if (_pause)
            {
                _pause = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return new(false, false, null, null, null, null, null, null, false, false, null, null, false, null, null, false, [], []);
        }
    }

    private sealed class EmptyRuntime : IAgentChatRuntime
    {
        public TaskCompletionSource<ChatTurnRequest> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(ChatTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requested.TrySetResult(request);
            await Task.CompletedTask;
            yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
        }
    }

    private sealed class SilentCompletion : IConversationCompletionNotifier
    {
        public void Notify(ConversationRecord conversation, IReadOnlyList<MessageRecord> messages) { }
    }
}
