using SelfClaw.Tests.TestDoubles;
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
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.ViewModels;

public sealed class MainWindowViewModelSubmissionTests
{
    [Fact]
    public async Task SubmitPromptAsync_accepts_direct_through_the_input_coordinator_without_executing_a_turn()
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository(conversation);
        var runtime = new RecordingAgentChatRuntime();
        var inputs = new FakeConversationInputCoordinator();
        var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var storagePaths = StoragePathDefaults.Create(
            storageRoot,
            Path.Combine(storageRoot, "selfclaw.db"),
            Path.Combine(storageRoot, "secrets"));

        try
        {
            var toolApprovalHandler = new DesktopToolApprovalHandler();
            using var activityCoordinator = new AgentActivityCoordinator(
                toolApprovalHandler,
                NullLogger<AgentActivityCoordinator>.Instance);
            var notificationService = new DesktopNotificationService(
                NullLogger<DesktopNotificationService>.Instance);
            var settingsStore = new DesktopSettingsJsonStore(storagePaths);
            var turns = new RecordingConversationTurnRepository(repository);
            var inputStore = new EmptyConversationInputRepository();
            using var runs = new ConversationRunCoordinator(inputStore, NullLogger<ConversationRunCoordinator>.Instance);
            var turnFinalizer = new DesktopTurnFinalizer(
                turns,
                NullLogger<DesktopTurnFinalizer>.Instance);
            var projection = new TranscriptProjection(storagePaths);
            using var delivery = new TranscriptDelivery(new WebViewHostChannel(), Dispatcher.CurrentDispatcher);
            using var transcriptPublisher = new TranscriptPublisher(
                projection,
                delivery,
                Dispatcher.CurrentDispatcher);
            using var sessions = new ConversationSessionCoordinator(repository, turns, runs, transcriptPublisher);
            var turnEngine = new ConversationTurnEngine(
                turns,
                turnFinalizer,
                new ConversationTurnRecorder(
                    repository, turns, inputStore,
                    NullLogger<ConversationTurnRecorder>.Instance),
                runtime,
                sessions, runs,
                activityCoordinator,
                toolApprovalHandler,
                SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(settingsStore),
                new ConversationCompletionNotifier(notificationService),
                NullLogger<ConversationTurnEngine>.Instance);
            var workspaces = new ConversationWorkspaceService(repository);
            var vm = new MainWindowViewModel(
                repository,
                turnEngine, runs, inputs,
                sessions,
                activityCoordinator,
                transcriptPublisher,
                new AgentSettingsService(new DesktopAgentDefinitionService(storagePaths),
                    new SubagentDefinitionCatalog(storagePaths),
                    new EmptyExtensionSettingsService(), new SelfClaw.Infrastructure.Extensions.ExtensionStateChangeNotifier()),
                new SelfClaw.Infrastructure.Extensions.ExtensionStateChangeNotifier(),
                settingsStore,
                workspaces,
                new ConversationDeletionService(runs, sessions, new NoOpSubagentConversationLifecycle(), workspaces, repository),
                NullLogger<MainWindowViewModel>.Instance);

            await vm.InitializeAsync();
            repository.CompleteMessages(conversation.Id, []);
            repository.CompleteToolRuns(conversation.Id, []);
            await vm.SelectConversationAsync(conversation.Id);

            var result = await vm.SubmitPromptAsync("first prompt");

            result.Accepted.Should().BeTrue();
            inputs.Submissions.Should().ContainSingle().Which.Prompt.Should().Be("first prompt");
            inputs.Submissions[0].ConversationId.Should().Be(conversation.Id);
            // The view model never starts a turn directly: Direct execution belongs to the dispatcher.
            runtime.Requests.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static ConversationRecord CreateConversation(Guid id)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationRecord(
            id,
            "Conversation",
            null,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
    }

    private sealed class RecordingAgentChatRuntime : IAgentChatRuntime
    {
        public List<ChatTurnRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield break;
        }
    }

    private sealed class ControlledConversationRepository : IConversationRepository, IWorkspaceRootRepository
    {
        private readonly ConversationRecord _conversation;
        private readonly TaskCompletionSource<IReadOnlyList<MessageRecord>> _messagesSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<ToolExecutionRecord>> _toolRunsSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledConversationRepository(ConversationRecord conversation)
        {
            _conversation = conversation;
        }

        public void CompleteMessages(Guid conversationId, IReadOnlyList<MessageRecord> messages)
            => _messagesSource.TrySetResult(messages);

        public void CompleteToolRuns(Guid conversationId, IReadOnlyList<ToolExecutionRecord> toolRuns)
            => _toolRunsSource.TrySetResult(toolRuns);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationRecord>>([_conversation]);

        public Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationRecord?>(_conversation.Id == conversationId ? _conversation : null);

        public Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
            => Task.FromResult(conversation);

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => _messagesSource.Task.WaitAsync(cancellationToken);

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => _toolRunsSource.Task.WaitAsync(cancellationToken);

        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);

        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}