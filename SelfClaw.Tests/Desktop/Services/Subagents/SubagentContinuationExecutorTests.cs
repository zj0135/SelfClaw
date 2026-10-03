using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentContinuationExecutorTests
{
    [Theory]
    [InlineData(RunCompletionStatus.Truncated, false)]
    [InlineData(RunCompletionStatus.Truncated, true)]
    [InlineData(RunCompletionStatus.Blocked, false)]
    [InlineData(RunCompletionStatus.Blocked, true)]
    public async Task Continuation_preserves_actual_turn_status_and_only_persists_with_atomic_delivery(
        RunCompletionStatus completion, bool hasTool)
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        await CompleteChildAsync(context, task);
        var parent = await context.Conversations.GetConversationAsync(task.ParentConversationId)
            ?? throw new InvalidOperationException("Missing parent.");
        var deliveries = new SqliteSubagentDeliveryRepository(context.Database);
        using var runs = new ConversationRunCoordinator(context.Inputs, NullLogger<ConversationRunCoordinator>.Instance);
        using var sessions = new ConversationSessionCoordinator(context.Conversations, context.Turns, runs, new Sink());
        var runtime = new TerminalRuntime(completion, hasTool, async request =>
        {
            (await context.Turns.ListTurnsAsync(parent.Id)).Should().NotContain(turn => turn.Id == request.TurnId);
            (await context.Conversations.ListMessagesAsync(parent.Id)).Should().NotContain(message => message.TurnId == request.TurnId);
        });
        var executor = new SubagentContinuationExecutor(deliveries, runtime, context.Recorder, context.Approvals,
            new SubagentTaskSnapshotSerializer(), new SubagentCompletionBatchSerializer(), runs, sessions,
            new DesktopNotificationService(NullLogger<DesktopNotificationService>.Instance), NullLogger<SubagentContinuationExecutor>.Instance);
        var attempts = completion == RunCompletionStatus.Truncated && !hasTool ? 3 : 1;
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var handle = await runs.TryReserveContinuationAsync(parent) ?? throw new InvalidOperationException("Reservation rejected.");
            var leaseAt = now.AddMinutes(attempt);
            var mailbox = await deliveries.PeekReadyMailboxAsync(leaseAt, leaseAt) ?? throw new InvalidOperationException("Missing mailbox.");
            var lease = await deliveries.TryLeaseBatchAsync(mailbox, Guid.NewGuid(), handle.TurnId, leaseAt, leaseAt.AddSeconds(45), 64 * 1024)
                ?? throw new InvalidOperationException("Missing lease.");
            await executor.ExecuteAsync(parent, handle, lease, CancellationToken.None);
            await handle.Completion;
            var delivery = await context.Tasks.GetDeliveryAsync(parent.Id, task.Id) ?? throw new InvalidOperationException();
            delivery.Status.Should().Be(attempt == attempts ? SubagentDeliveryStatus.DeadLetter : SubagentDeliveryStatus.Pending);
            delivery.AttemptCount.Should().Be(attempt);
            delivery.LastError.Should().Contain(completion == RunCompletionStatus.Blocked ? "Blocked by hook" : "output limit");
        }
        runtime.Runs.Should().Be(attempts);
        runtime.ToolCalls.Should().Be(hasTool ? 1 : 0);
        runs.IsRunning(parent.Id).Should().BeFalse();
        var turns = (await context.Turns.ListTurnsAsync(parent.Id)).Where(turn => turn.Origin == DirectTurnOrigin.Continuation).ToArray();
        var messages = (await context.Conversations.ListMessagesAsync(parent.Id)).Where(message => turns.Any(turn => turn.Id == message.TurnId)).ToArray();
        if (hasTool)
        {
            var turn = turns.Should().ContainSingle().Which;
            turn.Status.Should().Be(completion == RunCompletionStatus.Blocked ? ConversationTurnStatus.Blocked : ConversationTurnStatus.Truncated);
            var assistant = messages.Should().ContainSingle().Which;
            assistant.Id.Should().NotBe(turn.Id);
            assistant.TurnId.Should().Be(turn.Id);
            assistant.MarkdownContent.Should().Be("partial answer");
            (await context.Conversations.ListToolExecutionsAsync(parent.Id)).Where(tool => tool.MessageId == assistant.Id)
                .Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Completed);
        }
        else
        {
            turns.Should().BeEmpty();
            messages.Should().BeEmpty();
        }
    }

    private static async Task CompleteChildAsync(SubagentActivityTestContext context, SubagentTaskRecord task)
    {
        var turn = (await context.Turns.ListTurnsAsync(task.ChildConversationId)).Single(turn => turn.Id == task.ChildTurnId);
        var completedAt = DateTimeOffset.UtcNow;
        var message = new MessageRecord(Guid.NewGuid(), task.ChildConversationId, turn.Id,
            await context.Turns.ReserveMessageSequenceAsync(task.ChildConversationId), MessageRole.Assistant,
            "child result", MessageStatus.Sealed, completedAt, completedAt);
        await context.Tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Running,
            new SubagentTaskCompletion(SubagentTaskStatus.Succeeded,
                new(turn with { Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = completedAt }, [message], []),
                "child result", null, null, completedAt));
    }

    private sealed class TerminalRuntime(RunCompletionStatus status, bool hasTool, Func<ChatTurnRequest, Task> verifyDetached) : IAgentChatRuntime
    {
        public int Runs { get; private set; }
        public int ToolCalls { get; private set; }
        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(ChatTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Runs++;
            var direct = (DirectChatTurnRequest)request;
            direct.InputSession.Should().BeNull();
            await verifyDetached(request);
            yield return new AssistantThinkingDeltaEvent("thinking", "analysis");
            yield return new AssistantTextDeltaEvent("text", "partial answer");
            if (hasTool)
            {
                var checkpoint = direct.ToolExecutionCheckpoint ?? throw new InvalidOperationException("Missing checkpoint.");
                await checkpoint.BeforeExecutionAsync(cancellationToken);
                ToolCalls++;
                yield return new ToolCallStartedEvent("call", "write_file", "{}", ToolCallKind.Edit, ToolSourceKind.BuiltIn);
                yield return new ToolCallCompletedEvent("call", ToolCallStatus.Completed, "written", "done");
            }
            await verifyDetached(request);
            yield return new RunCompletedEvent(status, "partial answer", status == RunCompletionStatus.Blocked ? "Blocked by hook 'alpha/a': no." : null);
        }
    }

    private sealed class Sink : ITranscriptChangeSink
    {
        public void RequestStreamingPublish(bool autoScroll) { }
        public void PublishNow(bool autoScroll) { }
    }
}
