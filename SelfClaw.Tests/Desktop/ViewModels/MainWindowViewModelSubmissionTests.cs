using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Tools.Transcript;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Tests.Desktop.ViewModels;

public sealed class MainWindowViewModelSubmissionTests
{
    [Fact]
    public async Task SubmitPromptAsync_does_not_overwrite_the_first_prompt_when_two_submissions_wait_on_selection()
    {
        var conversation = CreateConversation(Guid.NewGuid());
        var repository = new ControlledConversationRepository(conversation);
        var runtime = new BlockingAgentChatRuntime();
        var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var storagePaths = new StoragePaths(
            storageRoot,
            Path.Combine(storageRoot, "selfclaw.db"),
            Path.Combine(storageRoot, "secrets"));

        try
        {
            var toolApprovalHandler = new DesktopToolApprovalHandler();
            using var activityCoordinator = new AgentActivityCoordinator(
                toolApprovalHandler,
                NullLogger<AgentActivityCoordinator>.Instance);
            using var notificationService = new DesktopNotificationService(
                NullLogger<DesktopNotificationService>.Instance);
            var settingsStore = new DesktopSettingsJsonStore(storagePaths);
            var turnFinalizer = new DesktopTurnFinalizer(
                new NoOpTurnFinalizationRepository(),
                NullLogger<DesktopTurnFinalizer>.Instance);
            var projection = new TranscriptProjection(storagePaths);
            using var transcriptPublisher = new TranscriptPublisher(
                projection,
                new WebViewHostChannel(),
                Dispatcher.CurrentDispatcher);
            using var sessions = new ConversationSessionCoordinator(repository, transcriptPublisher);
            using var turnEngine = new ConversationTurnEngine(
                repository,
                turnFinalizer,
                new ConversationTurnRecorder(
                    repository,
                    NullLogger<ConversationTurnRecorder>.Instance),
                runtime,
                sessions,
                activityCoordinator,
                toolApprovalHandler,
                new ProgrammingAssistantSettingsService(settingsStore),
                new SelfClaw.Tests.TestDoubles.StubAiProviderSettingsService(),
                new ConversationCompletionNotifier(notificationService),
                NullLogger<ConversationTurnEngine>.Instance);
            var vm = new MainWindowViewModel(
                repository,
                turnEngine,
                sessions,
                activityCoordinator,
                transcriptPublisher,
                new DesktopAgentDefinitionService(storagePaths),
                settingsStore,
                new SelfClaw.Tests.TestDoubles.NoOpSubagentConversationLifecycle(),
                NullLogger<MainWindowViewModel>.Instance);

            await vm.InitializeAsync();
            var selection = vm.SelectConversationAsync(conversation.Id);
            await repository.MessagesRequested.Task;

            var firstSubmission = vm.SubmitPromptAsync("first prompt");
            var secondSubmission = vm.SubmitPromptAsync("second prompt");

            repository.CompleteMessages(conversation.Id, []);
            repository.CompleteToolRuns(conversation.Id, []);
            await runtime.Requested.Task;
            await secondSubmission;

            runtime.Requests.Should().ContainSingle();
            runtime.Requests[0].Messages
                .Should().ContainSingle(message => message.MarkdownContent == "first prompt");
            repository.UpsertedMessages
                .Should().ContainSingle(message => message.MarkdownContent == "first prompt");

            runtime.Release();
            await firstSubmission;
            await selection;
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

    private sealed class BlockingAgentChatRuntime : IAgentChatRuntime
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ChatTurnRequest> Requests { get; } = [];

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Requested.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            yield break;
        }
    }

    private sealed class ControlledConversationRepository : IConversationRepository
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

        public TaskCompletionSource MessagesRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<MessageRecord> UpsertedMessages { get; } = [];

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
        {
            MessagesRequested.TrySetResult();
            return _messagesSource.Task.WaitAsync(cancellationToken);
        }

        public Task<MessageRecord> UpsertMessageAsync(MessageRecord message, CancellationToken cancellationToken = default)
        {
            UpsertedMessages.Add(message);
            return Task.FromResult(message);
        }

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => _toolRunsSource.Task.WaitAsync(cancellationToken);

        public Task<ToolExecutionRecord> UpsertToolExecutionAsync(ToolExecutionRecord record, CancellationToken cancellationToken = default)
            => Task.FromResult(record);

        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);

        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpTurnFinalizationRepository : ITurnFinalizationRepository
    {
        public Task<bool> TryFinalizeTurnAsync(
            TurnFinalization finalization,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
