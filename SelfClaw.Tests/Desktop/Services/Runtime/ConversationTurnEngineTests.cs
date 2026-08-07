using System.Runtime.CompilerServices;
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
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationTurnEngineTests
{
    [Fact]
    public async Task ExecuteAsync_runs_a_successful_turn_through_persistence_projection_and_notification()
    {
        var repository = new FakeConversationRepository();
        var runtime = new RecordingAgentChatRuntime
        {
            Events =
            [
                new RunStartedEvent("session", "model", null),
                new AssistantTextDeltaEvent("block", "hello "),
                new ToolCallStartedEvent(
                    "call-1",
                    "mcp__git__status",
                    "{}",
                    ToolCallKind.Read,
                    ToolSourceKind.Mcp,
                    "git",
                    "status"),
                new ToolCallCompletedEvent("call-1", ToolCallStatus.Completed, "read 1 file", "contents"),
                new AssistantTextDeltaEvent("block", "world"),
                new UsageReportedEvent(11, 7),
                new RunCompletedEvent(RunCompletionStatus.Succeeded, "hello world")
            ]
        };
        using var context = new EngineTestContext(repository, runtime);

        var conversation = await context.ExecuteAsync(CreateRequest());

        conversation.Should().NotBeNull();
        conversation!.Title.Should().Be("prompt");
        repository.ConversationUpserts.Should().ContainSingle();
        repository.MessageUpserts.Should().ContainSingle(message =>
            message.Role == MessageRole.User && message.MarkdownContent == "prompt");
        runtime.Requests.Should().ContainSingle()
            .Which.Messages.Should().ContainSingle(message =>
                message.Role == MessageRole.User && message.MarkdownContent == "prompt");
        var finalization = context.FinalizationRepository.Finalizations.Should().ContainSingle().Which;
        var assistant = finalization.AssistantMessage;
        assistant.Status.Should().Be(MessageStatus.Completed);
        assistant.MarkdownContent.Should().Contain("hello ").And.Contain("world").And.Contain("selfclaw:tool:");
        assistant.InputTokens.Should().Be(11);
        assistant.OutputTokens.Should().Be(7);
        runtime.Requests.Single().TurnId.Should().Be(assistant.Id);
        finalization.ToolExecutions.Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Completed);
        finalization.ToolExecutions[0].SourceKind.Should().Be(ToolSourceKind.Mcp);
        finalization.ToolExecutions[0].SourceId.Should().Be("git");
        finalization.ToolExecutions[0].DisplayName.Should().Be("status");
        repository.ToolUpserts.Should().Be(2);
        context.Notifier.Notifications.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_maps_failed_completion_to_failed_message()
    {
        var runtime = new RecordingAgentChatRuntime
        {
            Events =
            [
                new RunStartedEvent(null, null, null),
                new RunCompletedEvent(RunCompletionStatus.Failed, null, "provider exploded")
            ]
        };
        using var context = new EngineTestContext(new FakeConversationRepository(), runtime);

        await context.ExecuteAsync(CreateRequest());

        var assistant = context.FinalizationRepository.Finalizations.Single().AssistantMessage;
        assistant.Status.Should().Be(MessageStatus.Failed);
        assistant.ErrorMessage.Should().Be("provider exploded");
        context.Notifier.Notifications.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_cancels_the_message_and_running_tool_when_the_user_stops_the_turn()
    {
        var runtime = new RecordingAgentChatRuntime
        {
            Events =
            [
                new AssistantTextDeltaEvent("block", "partial"),
                new ToolCallStartedEvent("call-1", "run_shell_command", "{}", ToolCallKind.Run)
            ],
            Failure = new OperationCanceledException("stopped")
        };
        using var context = new EngineTestContext(new FakeConversationRepository(), runtime);
        runtime.BeforeFailure = context.StopSelected;

        await context.ExecuteAsync(CreateRequest());

        var finalization = context.FinalizationRepository.Finalizations.Single();
        var assistant = finalization.AssistantMessage;
        assistant.Status.Should().Be(MessageStatus.Cancelled);
        assistant.MarkdownContent.Should().Contain("partial");
        assistant.ErrorMessage.Should().Be("Generation stopped.");
        finalization.ToolExecutions.Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Cancelled);
        context.Notifier.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ignores_a_late_duplicate_terminal_event()
    {
        var runtime = new RecordingAgentChatRuntime
        {
            Events =
            [
                new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"),
                new RunCompletedEvent(RunCompletionStatus.Failed, null, "late failure")
            ]
        };
        using var context = new EngineTestContext(new FakeConversationRepository(), runtime);

        await context.ExecuteAsync(CreateRequest());

        context.FinalizationRepository.Finalizations.Single().AssistantMessage
            .Status.Should().Be(MessageStatus.Completed);
        context.Notifier.Notifications.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_preserves_an_existing_title_and_updates_turn_settings()
    {
        var runtime = new RecordingAgentChatRuntime
        {
            Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done")]
        };
        var repository = new FakeConversationRepository();
        using var context = new EngineTestContext(repository, runtime);
        var now = DateTimeOffset.UtcNow;
        var existing = new ConversationRecord(
            Guid.NewGuid(),
            "Keep this title",
            null,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        var workspace = new WorkspaceRoot(Guid.NewGuid(), "Workspace", "C:\\workspace", now, now);

        await context.ExecuteAsync(new DesktopConversationTurnRequest(
            existing,
            CreateAgent(),
            "new prompt",
            null,
            workspace,
            ToolPermissionMode.FullAccess));

        var persisted = repository.ConversationUpserts.Should().ContainSingle().Which;
        persisted.Title.Should().Be("Keep this title");
        persisted.WorkspaceRootId.Should().Be(workspace.Id);
        persisted.ToolPermissionMode.Should().Be(ToolPermissionMode.FullAccess);
    }

    [Fact]
    public async Task ExecuteAsync_builds_the_cli_request_inside_the_turn_module()
    {
        var runtime = new RecordingAgentChatRuntime
        {
            Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done")]
        };
        using var context = new EngineTestContext(new FakeConversationRepository(), runtime);

        await context.ExecuteAsync(CreateRequest(mode: AgentExecutionMode.Cli));

        var chatRequest = runtime.Requests.Should().ContainSingle().Which;
        chatRequest.Should().BeOfType<CliChatTurnRequest>();
        chatRequest.TurnId.Should().Be(
            context.FinalizationRepository.Finalizations.Single().AssistantMessage.Id);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_a_second_running_turn_during_admission()
    {
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Conversation",
            null,
            ToolPermissionMode.RequireApproval,
            "build",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var runtime = new BlockingAgentChatRuntime();
        using var context = new EngineTestContext(new FakeConversationRepository(), runtime);

        var firstTurn = context.ExecuteAsync(CreateRequest(conversation));
        await runtime.Requested.Task;
        var secondAdmission = await context.ExecuteAsync(CreateRequest(conversation));

        secondAdmission.Should().BeNull();
        runtime.Requests.Should().ContainSingle();
        runtime.Release();
        await firstTurn;
    }

    [Fact]
    public async Task ExecuteAsync_propagates_terminal_persistence_cancellation_and_projects_failure()
    {
        var runtime = new RecordingAgentChatRuntime
        {
            Events = [new RunCompletedEvent(RunCompletionStatus.Succeeded, "done")]
        };
        using var context = new EngineTestContext(
            new FakeConversationRepository(),
            runtime,
            new CancellingFinalizationRepository());

        var action = () => context.ExecuteAsync(CreateRequest());

        await action.Should().ThrowAsync<OperationCanceledException>();
        var assistant = context.SelectedMessages.Single(message => message.Role == MessageRole.Assistant);
        assistant.Status.Should().Be(MessageStatus.Failed);
        assistant.ErrorMessage.Should().Contain("Failed to persist terminal state");
        context.Notifier.Notifications.Should().BeEmpty();
    }

    private static DesktopConversationTurnRequest CreateRequest(
        ConversationRecord? conversation = null,
        AgentExecutionMode mode = AgentExecutionMode.Direct)
        => new(
            conversation,
            CreateAgent(mode),
            "prompt",
            null,
            null,
            ToolPermissionMode.RequireApproval);

    private static AgentRuntimeDefinition CreateAgent(
        AgentExecutionMode mode = AgentExecutionMode.Direct)
        => new(
            "build",
            "Builder",
            "test",
            mode,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            [],
            "");

    private sealed class EngineTestContext : IDisposable
    {
        private readonly AgentActivityCoordinator _activityCoordinator;
        private readonly ConversationSessionCoordinator _sessions;

        public EngineTestContext(
            FakeConversationRepository repository,
            IAgentChatRuntime runtime,
            ITurnFinalizationRepository? finalizationRepository = null)
        {
            var approvalHandler = new DesktopToolApprovalHandler();
            _activityCoordinator = new AgentActivityCoordinator(
                approvalHandler,
                NullLogger<AgentActivityCoordinator>.Instance);
            _sessions = new ConversationSessionCoordinator(repository, new NoOpTranscriptChangeSink());
            var storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
            var settingsStore = new DesktopSettingsJsonStore(new StoragePaths(
                storageRoot,
                Path.Combine(storageRoot, "selfclaw.db"),
                Path.Combine(storageRoot, "secrets")));
            Notifier = new RecordingCompletionNotifier();
            FinalizationRepository = finalizationRepository as RecordingFinalizationRepository
                ?? new RecordingFinalizationRepository();
            Engine = new ConversationTurnEngine(
                repository,
                new DesktopTurnFinalizer(
                    finalizationRepository ?? FinalizationRepository,
                    NullLogger<DesktopTurnFinalizer>.Instance),
                new ConversationTurnRecorder(
                    repository,
                    NullLogger<ConversationTurnRecorder>.Instance),
                runtime,
                _sessions,
                _activityCoordinator,
                approvalHandler,
                new ProgrammingAssistantSettingsService(settingsStore),
                Notifier,
                NullLogger<ConversationTurnEngine>.Instance);
        }

        public ConversationTurnEngine Engine { get; }

        public RecordingFinalizationRepository FinalizationRepository { get; }

        public RecordingCompletionNotifier Notifier { get; }

        public IReadOnlyList<MessageRecord> SelectedMessages => _sessions.SelectedMessages;

        public async Task<ConversationRecord?> ExecuteAsync(DesktopConversationTurnRequest request)
        {
            var admission = await Engine.TryAdmitAsync(request);
            if (admission is null)
            {
                return null;
            }

            await _sessions.SelectAsync(admission.Conversation.Id);
            await Engine.ExecuteAsync(admission);
            return admission.Conversation;
        }

        public void StopSelected() => _sessions.StopSelected();

        public void Dispose()
        {
            Engine.Dispose();
            _sessions.Dispose();
            _activityCoordinator.Dispose();
        }
    }

    private sealed class RecordingAgentChatRuntime : IAgentChatRuntime
    {
        public IReadOnlyList<AgentStreamEvent> Events { get; init; } = [];

        public Action? BeforeFailure { get; set; }

        public Exception? Failure { get; init; }

        public List<ChatTurnRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var streamEvent in Events)
            {
                yield return streamEvent;
            }

            BeforeFailure?.Invoke();
            if (Failure is not null)
            {
                throw Failure;
            }

            await Task.CompletedTask;
        }
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
            yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
        }
    }

    private sealed class RecordingCompletionNotifier : IConversationCompletionNotifier
    {
        public List<Guid> Notifications { get; } = [];

        public void Notify(ConversationRecord conversation, IReadOnlyList<MessageRecord> messages)
            => Notifications.Add(conversation.Id);
    }

    private sealed class NoOpTranscriptChangeSink : ITranscriptChangeSink
    {
        public void RequestStreamingPublish(bool autoScroll)
        {
        }

        public void PublishNow(bool autoScroll)
        {
        }
    }

    private sealed class FakeConversationRepository : IConversationRepository
    {
        public int ToolUpserts { get; private set; }

        public List<ConversationRecord> ConversationUpserts { get; } = [];

        public List<MessageRecord> MessageUpserts { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationRecord?>(null);

        public Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
        {
            ConversationUpserts.Add(conversation);
            return Task.FromResult(conversation);
        }

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MessageRecord>>([]);

        public Task<MessageRecord> UpsertMessageAsync(MessageRecord message, CancellationToken cancellationToken = default)
        {
            MessageUpserts.Add(message);
            return Task.FromResult(message);
        }

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);

        public Task<ToolExecutionRecord> UpsertToolExecutionAsync(
            ToolExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            ToolUpserts++;
            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);

        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingFinalizationRepository : ITurnFinalizationRepository
    {
        public List<TurnFinalization> Finalizations { get; } = [];

        public Task<bool> TryFinalizeTurnAsync(
            TurnFinalization finalization,
            CancellationToken cancellationToken = default)
        {
            Finalizations.Add(finalization);
            return Task.FromResult(true);
        }
    }

    private sealed class CancellingFinalizationRepository : ITurnFinalizationRepository
    {
        public Task<bool> TryFinalizeTurnAsync(
            TurnFinalization finalization,
            CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new OperationCanceledException("database timeout"));
    }
}
