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
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentContinuationExecutorTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_truncation_retries_without_tools_and_dead_letters_with_tools(bool hasTool)
    {
        var paths = StoragePathDefaults.Create(_rootPath, Path.Combine(_rootPath, "continuation.db"), Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(paths);
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        var deliveries = new SqliteSubagentDeliveryRepository(database);
        await tasks.InitializeAsync();
        var task = await SubagentTaskTestData.CreateRunningTaskAsync(conversations, tasks);
        var now = DateTimeOffset.UtcNow;
        var childMessage = new MessageRecord(task.ChildTurnId, task.ChildConversationId, MessageRole.Assistant,
            "child result", MessageStatus.Completed, now, now);
        await tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Running,
            new SubagentTaskCompletion(SubagentTaskStatus.Succeeded, new TurnFinalization(childMessage, []),
                "child result", null, null, now));
        var parent = await conversations.GetConversationAsync(task.ParentConversationId)
            ?? throw new InvalidOperationException("Missing parent.");
        var approvalHandler = new DesktopToolApprovalHandler();
        using var activity = new AgentActivityCoordinator(approvalHandler, NullLogger<AgentActivityCoordinator>.Instance);
        using var sessions = new ConversationSessionCoordinator(conversations, new NoOpTranscriptChangeSink());
        using var notifications = new DesktopNotificationService(NullLogger<DesktopNotificationService>.Instance);
        var runtime = new TruncatedRuntime(hasTool);
        var recorder = new ConversationTurnRecorder(conversations, NullLogger<ConversationTurnRecorder>.Instance);
        using var engine = new ConversationTurnEngine(
            conversations, new DesktopTurnFinalizer(conversations, NullLogger<DesktopTurnFinalizer>.Instance),
            recorder, runtime, sessions, activity, approvalHandler,
            new ProgrammingAssistantSettingsService(new DesktopSettingsJsonStore(paths)),
            new NullCompletionNotifier(), NullLogger<ConversationTurnEngine>.Instance);
        var executor = new SubagentContinuationExecutor(deliveries, runtime, recorder, approvalHandler,
            new SubagentTaskSnapshotSerializer(), new SubagentCompletionBatchSerializer(), engine, notifications,
            NullLogger<SubagentContinuationExecutor>.Instance);

        var expectedAttempts = hasTool ? 1 : 3;
        for (var attempt = 1; attempt <= expectedAttempts; attempt++)
        {
            var leaseAt = now.AddMinutes(attempt);
            var mailbox = await deliveries.PeekReadyMailboxAsync(leaseAt, leaseAt)
                ?? throw new InvalidOperationException("Missing ready mailbox.");
            var lease = await deliveries.TryLeaseBatchAsync(mailbox, Guid.NewGuid(), Guid.NewGuid(),
                leaseAt, leaseAt.AddSeconds(45), 64 * 1024)
                ?? throw new InvalidOperationException("Missing lease.");
            var state = await engine.TryAdmitContinuationAsync(parent, CancellationToken.None)
                ?? throw new InvalidOperationException("Continuation admission failed.");
            await executor.ExecuteAsync(parent, state, lease, CancellationToken.None);
            var delivery = await tasks.GetDeliveryAsync(parent.Id, task.Id)
                ?? throw new InvalidOperationException("Missing delivery.");
            delivery.Status.Should().Be(attempt == expectedAttempts ? SubagentDeliveryStatus.DeadLetter : SubagentDeliveryStatus.Pending);
            delivery.AttemptCount.Should().Be(attempt);
            delivery.LastError.Should().Contain("output limit");
        }

        runtime.Runs.Should().Be(expectedAttempts);
        runtime.ToolCalls.Should().Be(hasTool ? 1 : 0);
        (await deliveries.PeekReadyMailboxAsync(now.AddHours(1), now.AddHours(1))).Should().BeNull();
        var messages = await conversations.ListMessagesAsync(parent.Id);
        if (hasTool)
        {
            var assistant = messages.Should().ContainSingle().Which;
            assistant.Status.Should().Be(MessageStatus.Failed);
            assistant.MarkdownContent.Should().Be("partial answer");
            assistant.Segments.Should().Contain(segment => segment.Kind == MessageSegmentKind.Thinking);
            (await conversations.ListToolExecutionsAsync(parent.Id)).Should().ContainSingle()
                .Which.Status.Should().Be(ToolExecutionStatus.Completed);
        }
        else
        {
            messages.Should().BeEmpty();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TruncatedRuntime(bool hasTool) : IAgentChatRuntime
    {
        internal int Runs { get; private set; }
        internal int ToolCalls { get; private set; }

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Runs++;
            yield return new AssistantThinkingDeltaEvent("thinking", "analysis");
            yield return new AssistantTextDeltaEvent("text", "partial answer");
            if (hasTool)
            {
                var direct = (DirectChatTurnRequest)request;
                var checkpoint = direct.ToolExecutionCheckpoint ?? throw new InvalidOperationException("Missing checkpoint.");
                await checkpoint.BeforeExecutionAsync(cancellationToken);
                ToolCalls++;
                yield return new ToolCallStartedEvent("call-1", "write_file", "{}", ToolCallKind.Edit, ToolSourceKind.BuiltIn);
                yield return new ToolCallCompletedEvent("call-1", ToolCallStatus.Completed, "written", "done");
            }

            await Task.Yield();
            yield return new RunCompletedEvent(RunCompletionStatus.Truncated, "partial answer");
        }
    }

    private sealed class NullCompletionNotifier : IConversationCompletionNotifier
    {
        public void Notify(ConversationRecord conversation, IReadOnlyList<MessageRecord> messages) { }
    }

    private sealed class NoOpTranscriptChangeSink : ITranscriptChangeSink
    {
        public void RequestStreamingPublish(bool autoScroll) { }
        public void PublishNow(bool autoScroll) { }
    }
}
