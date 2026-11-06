using SelfClaw.Tests.TestDoubles;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Agents.Definitions;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
using SelfClaw.Desktop.Services.Questions;
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
        using var harness = await Harness.CreateAsync(CreateConversation(Guid.NewGuid(), DateTimeOffset.UtcNow));

        var result = await harness.ViewModel.SubmitPromptAsync("first prompt");

        result.Accepted.Should().BeTrue();
        harness.Inputs.Submissions.Should().ContainSingle().Which.Prompt.Should().Be("first prompt");
        harness.Inputs.Submissions[0].ConversationId.Should().Be(harness.Conversation.Id);
        // The view model never starts a turn directly: Direct execution belongs to the dispatcher.
        harness.Runtime.Requests.Should().BeEmpty();
    }

    // 再发一条消息就是「最后一次对话时间」前进：不刷新它，刚发过消息的会话会继续赖在侧栏归档里，
    // 而列表重载只发生在初始化 / 删除 / 重命名之后。
    [Fact]
    public async Task SubmitPromptAsync_refreshes_the_navigation_timestamp_of_an_existing_conversation()
    {
        var stale = DateTimeOffset.UtcNow.AddDays(-30);
        using var harness = await Harness.CreateAsync(CreateConversation(Guid.NewGuid(), stale));

        (await harness.ViewModel.SubmitPromptAsync("follow up")).Accepted.Should().BeTrue();

        var published = harness.LastNavigationTimestamp();
        published.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    // 自动总结标题在回合中途写库并重载列表，而 composer 的会话快照仍是首条消息的截断标题：
    // 回合没结束时再发一条（steer）如果拿这份快照回填，侧栏就会退回「用户的第一句话」。
    [Fact]
    public async Task SubmitPromptAsync_keeps_a_title_that_changed_outside_the_composer_snapshot()
    {
        using var harness = await Harness.CreateAsync(CreateConversation(Guid.NewGuid(), DateTimeOffset.UtcNow));
        await harness.ViewModel.RenameConversationAsync(harness.Conversation.Id, "Summarized title");

        (await harness.ViewModel.SubmitPromptAsync("steer the running turn")).Accepted.Should().BeTrue();

        harness.LastNavigationTitle().Should().Be("Summarized title",
            "a second message must not resurrect the staleness of the composer's conversation snapshot");
        harness.ViewModel.SelectedConversation!.Title.Should().Be("Summarized title",
            "the selected conversation must follow the reloaded record, not the snapshot it was selected with");
    }

    private static ConversationRecord CreateConversation(Guid id, DateTimeOffset updatedAtUtc)
        => new(
            id,
            "Conversation",
            null,
            ToolPermissionMode.RequireApproval,
            "build",
            updatedAtUtc,
            updatedAtUtc);

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
        private ConversationRecord _conversation;
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
        {
            if (conversation.Id == _conversation.Id) _conversation = conversation;
            return Task.FromResult(conversation);
        }

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

    // 整条 ViewModel 链路的装配只写一次：宿主消息列表就是 shell 发出去的 JSON，
    // 断言导航数据不必为了拿到它另开一条内存通道。
    private sealed class Harness : IDisposable
    {
        private readonly string _storageRoot;
        private readonly AgentActivityCoordinator _activityCoordinator;
        private readonly ConversationRunCoordinator _runs;
        private readonly TranscriptDelivery _delivery;
        private readonly TranscriptPublisher _transcriptPublisher;

        private Harness(ConversationRecord conversation, string storageRoot, AgentActivityCoordinator activityCoordinator,
            ConversationRunCoordinator runs, TranscriptDelivery delivery, TranscriptPublisher transcriptPublisher,
            FakeConversationInputCoordinator inputs,
            RecordingAgentChatRuntime runtime, MainWindowViewModel viewModel, List<string> hostMessages)
        {
            Conversation = conversation;
            _storageRoot = storageRoot;
            _activityCoordinator = activityCoordinator;
            _runs = runs;
            _delivery = delivery;
            _transcriptPublisher = transcriptPublisher;
            Inputs = inputs;
            Runtime = runtime;
            ViewModel = viewModel;
            HostMessages = hostMessages;
        }

        public ConversationRecord Conversation { get; }
        public FakeConversationInputCoordinator Inputs { get; }
        public RecordingAgentChatRuntime Runtime { get; }
        public MainWindowViewModel ViewModel { get; }
        public List<string> HostMessages { get; }

        public static async Task<Harness> CreateAsync(ConversationRecord conversation)
        {
            var repository = new ControlledConversationRepository(conversation);
            var runtime = new RecordingAgentChatRuntime();
            var inputs = new FakeConversationInputCoordinator();
            var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
            var storagePaths = StoragePathDefaults.Create(
                storageRoot,
                Path.Combine(storageRoot, "selfclaw.db"),
                Path.Combine(storageRoot, "secrets"));
            var toolApprovalHandler = new DesktopToolApprovalHandler();
            var userQuestionHandler = new DesktopUserQuestionHandler();
            var activityCoordinator = new AgentActivityCoordinator(
                toolApprovalHandler,
                userQuestionHandler,
                NullLogger<AgentActivityCoordinator>.Instance);
            var notificationService = new DesktopNotificationService(
                NullLogger<DesktopNotificationService>.Instance);
            var settingsStore = new DesktopSettingsJsonStore(storagePaths);
            var turns = new RecordingConversationTurnRepository(repository);
            var inputStore = new EmptyConversationInputRepository();
            var runs = new ConversationRunCoordinator(inputStore, new EmptyConversationInputSteeringStore(), new ConversationInputChangeNotifier(), NullLogger<ConversationRunCoordinator>.Instance);
            var turnFinalizer = new DesktopTurnFinalizer(
                turns,
                NullLogger<DesktopTurnFinalizer>.Instance);
            var projection = new TranscriptProjection(storagePaths);
            var hostMessages = new List<string>();
            var channel = new WebViewHostChannel();
            TranscriptDelivery? delivery = null;
            // 宿主不 ack 就只有第一条 payload 能出去：把每条待发快照都确认掉，才能读到最新一次发布。
            channel.Attach(json =>
            {
                hostMessages.Add(json);
                delivery?.Acknowledge(ReadRevision(json));
            });
            delivery = new TranscriptDelivery(channel, Dispatcher.CurrentDispatcher);
            channel.MarkReady();
            var transcriptPublisher = new TranscriptPublisher(
                projection,
                delivery,
                Dispatcher.CurrentDispatcher);
            var sessions = new ConversationSessionCoordinator(repository, turns, new EmptyConversationCompactionRepository(), runs, transcriptPublisher);
            var titleService = new ConversationTitleService(
                new SelfClaw.Tests.TestDoubles.NoOpConversationTitleGenerator(),
                repository,
                NullLogger<ConversationTitleService>.Instance);
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
                userQuestionHandler,
                SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(settingsStore),
                new ConversationCompletionNotifier(notificationService),
                titleService,
                NullLogger<ConversationTurnEngine>.Instance);
            var workspaces = new ConversationWorkspaceService(repository);
            var viewModel = new MainWindowViewModel(
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
                titleService,
                NullLogger<MainWindowViewModel>.Instance);

            var harness = new Harness(conversation, storageRoot, activityCoordinator, runs, delivery, transcriptPublisher,
                inputs, runtime, viewModel, hostMessages);
            try
            {
                await viewModel.InitializeAsync();
                repository.CompleteMessages(conversation.Id, []);
                repository.CompleteToolRuns(conversation.Id, []);
                await viewModel.SelectConversationAsync(conversation.Id);
                return harness;
            }
            catch
            {
                harness.Dispose();
                throw;
            }
        }

        private static long ReadRevision(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.GetProperty("revision").GetInt64();
        }

        public DateTimeOffset LastNavigationTimestamp()
            => DateTimeOffset.Parse(LastNavigationItem().GetProperty("updatedAtUtc").GetString()!);

        public string LastNavigationTitle() => LastNavigationItem().GetProperty("title").GetString()!;

        private JsonElement LastNavigationItem()
        {
            // 补丁只有导航真的变了才带 conversations：倒着找最近一条真正装着导航数据的上报。
            for (var index = HostMessages.Count - 1; index >= 0; index--)
            {
                using var payload = JsonDocument.Parse(HostMessages[index]);
                if (!payload.RootElement.TryGetProperty("conversations", out var conversations)) continue;
                foreach (var conversation in conversations.EnumerateArray())
                {
                    if (conversation.GetProperty("id").GetString() == Conversation.Id.ToString("D")) return conversation.Clone();
                }
            }

            throw new InvalidOperationException("No published transcript payload carried navigation data.");
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _transcriptPublisher.Dispose();
            _delivery.Dispose();
            _runs.Dispose();
            _activityCoordinator.Dispose();
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
    }
}
