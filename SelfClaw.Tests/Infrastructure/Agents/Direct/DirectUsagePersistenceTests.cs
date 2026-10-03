using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

public sealed class DirectUsagePersistenceTests
{
    [Theory]
    [InlineData("incremental-unknown")]
    [InlineData("cumulative-partial-error")]
    [InlineData("incremental-complete")]
    public async Task Interactive_usage_survives_runtime_recorder_finalization_and_sqlite(string scenario)
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var (runtime, initial) = CreateRuntime(scenario);
        var request = new DirectChatTurnRequest(context.Record.Id, context.Conversation.Id, initial.WorkspaceRoot,
            initial.Agent, context.State.Messages, context.State.Turns, initial.ModelProfileId,
            initial.ToolPermissionMode, initial.ToolApprovalHandler, initial.ExecutionContext);
        var reported = await RecordAsync(runtime, request, context.ApplyAsync);

        context.State.Turns.Single(turn => turn.Id == request.TurnId).Usage.Should().BeEquivalentTo(reported);
        await AssertStoredAsync(context.Database, context.Turns, request.ConversationId, request.TurnId, reported, scenario);
    }

    [Theory]
    [InlineData("incremental-unknown")]
    [InlineData("cumulative-partial-error")]
    [InlineData("incremental-complete")]
    public async Task Child_usage_survives_live_snapshot_and_atomic_task_completion(string scenario)
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        await using var session = await context.RegisterSessionAsync(task);
        await session.BeginAsync();
        var (runtime, initial) = CreateRuntime(scenario);
        var request = new DirectChatTurnRequest(task.ChildTurnId, task.ChildConversationId, initial.WorkspaceRoot,
            initial.Agent, session.InitialMessages, session.InitialTurns, initial.ModelProfileId, initial.ToolPermissionMode, initial.ToolApprovalHandler,
            new(DirectTurnOrigin.Subagent, new DirectCapabilityCeiling("system", [], [], [], []), null));
        var reported = await RecordAsync(runtime, request, async item =>
        {
            await session.ApplyEventAsync(item, CancellationToken.None);
            if (item is UsageReportedEvent usage)
            {
                var snapshot = await session.CaptureSnapshotAsync() ?? throw new InvalidOperationException("Missing live snapshot.");
                var liveTurn = snapshot.Turn ?? throw new InvalidOperationException("Missing live turn.");
                liveTurn.Usage.Should().BeEquivalentTo(usage.Usage);
            }
        });
        await AssertStoredAsync(context.Database, context.Turns, task.ChildConversationId, task.ChildTurnId, reported, scenario);
        var persistedTask = await context.Tasks.GetAsync(task.ParentConversationId, task.Id);
        persistedTask!.InputTokens.Should().Be(reported.InputTokens);
        persistedTask.OutputTokens.Should().Be(reported.OutputTokens);
        (await context.Conversations.ListMessagesAsync(task.ParentConversationId)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("incremental-unknown")]
    [InlineData("cumulative-partial-error")]
    [InlineData("incremental-complete")]
    public async Task Continuation_usage_stays_detached_until_atomic_delivery_resolution(string scenario)
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var now = DateTimeOffset.UtcNow;
        var childTurn = (await context.Turns.ListTurnsAsync(task.ChildConversationId)).Single();
        var childMessageId = Guid.NewGuid();
        var childMessage = new MessageRecord(childMessageId, task.ChildConversationId, task.ChildTurnId,
            await context.Turns.ReserveMessageSequenceAsync(task.ChildConversationId), MessageRole.Assistant,
            "child result", MessageStatus.Sealed, now, now,
            Segments: [new MessageSegmentRecord(childMessageId, 0, MessageSegmentKind.Text, "child result", null)]);
        await context.Tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Running, new SubagentTaskCompletion(
            SubagentTaskStatus.Succeeded, new ConversationTurnCommit(childTurn with
            { Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = now }, [childMessage], []), "child result", null, null, now));
        var deliveries = new SqliteSubagentDeliveryRepository(context.Database);
        var mailbox = await deliveries.PeekReadyMailboxAsync(now.AddSeconds(3), now.AddSeconds(3))
            ?? throw new InvalidOperationException("Missing mailbox.");
        var lease = await deliveries.TryLeaseBatchAsync(mailbox, Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(3), now.AddMinutes(1), 65536)
            ?? throw new InvalidOperationException("Missing lease.");
        var parent = await context.Conversations.GetConversationAsync(task.ParentConversationId)
            ?? throw new InvalidOperationException("Missing parent.");
        var committer = new SubagentContinuationTurnCommitter(deliveries, lease, TimeProvider.System);
        var detachedTurn = new ConversationTurnRecord(lease.ContinuationTurnId, parent.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Continuation, ConversationTurnStatus.Running, now);
        var state = new ConversationRuntimeState(parent, [detachedTurn], [], [], isDetached: true);
        var (runtime, initial) = CreateRuntime(scenario);
        var request = new DirectChatTurnRequest(lease.ContinuationTurnId, parent.Id, initial.WorkspaceRoot,
            initial.Agent, state.Messages, state.Turns, initial.ModelProfileId, initial.ToolPermissionMode, initial.ToolApprovalHandler,
            new(DirectTurnOrigin.Continuation, new DirectCapabilityCeiling("system", [], [], [], []), null),
            ToolExecutionCheckpoint: committer);
        var turn = new AgentTurnState(detachedTurn, request.Agent);
        context.Recorder.BeginTurn(state, turn);
        var reported = await RecordAsync(runtime, request, async item =>
        {
            if (item is RunCompletedEvent)
            {
                (await context.Conversations.ListMessagesAsync(parent.Id)).Should().BeEmpty("the continuation has not committed yet");
                (await context.Turns.ListTurnsAsync(parent.Id)).Should().BeEmpty("even the detached Running turn is not persisted");
                await using var connection = await context.Database.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM turn_usage WHERE turn_id = $id";
                command.Parameters.AddWithValue("$id", request.TurnId.ToString());
                (await command.ExecuteScalarAsync()).Should().Be(0L);
            }
            await context.Recorder.ApplyDetachedEventAsync(state, turn, item, committer, CancellationToken.None);
        });
        await AssertStoredAsync(context.Database, context.Turns, parent.Id, request.TurnId, reported, scenario);
        var delivery = await deliveries.GetAsync(parent.Id, task.Id);
        delivery!.Status.Should().Be(scenario == "cumulative-partial-error" ? SubagentDeliveryStatus.DeadLetter : SubagentDeliveryStatus.Delivered);
    }

    private static async Task<TurnUsage> RecordAsync(DirectAgentChatRuntime runtime, DirectChatTurnRequest request,
        Func<AgentStreamEvent, Task> apply)
    {
        var reports = new List<TurnUsage>();
        var terminals = 0;
        await foreach (var item in runtime.StreamTurnAsync(request))
        {
            if (item is UsageReportedEvent usage) reports.Add(usage.Usage);
            if (item is RunCompletedEvent) terminals++;
            await apply(item);
        }
        terminals.Should().Be(1);
        return reports.Should().ContainSingle().Subject;
    }

    private static async Task AssertStoredAsync(SqliteDatabase database, IConversationTurnRepository turns, Guid conversationId, Guid turnId,
        TurnUsage reported, string scenario)
    {
        reported.TotalTokens.Should().Be(scenario == "incremental-complete" ? 17 : null);
        reported.InputTokens.Should().Be(scenario == "cumulative-partial-error" ? 17 : 3);
        reported.OutputTokens.Should().Be(scenario == "cumulative-partial-error" ? 2 : 4);
        reported.ProviderCalls.Should().Be(scenario == "cumulative-partial-error" ? 2 : 1);
        var persisted = (await turns.ListTurnsAsync(conversationId)).Single(turn => turn.Id == turnId);
        persisted.Usage.Should().BeEquivalentTo(reported);
        persisted.Status.Should().Be(scenario == "cumulative-partial-error" ? ConversationTurnStatus.Failed : ConversationTurnStatus.Succeeded);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT total_tokens FROM turn_usage WHERE turn_id = $id";
        command.Parameters.AddWithValue("$id", turnId.ToString());
        var value = await command.ExecuteScalarAsync();
        if (scenario == "incremental-complete") value.Should().Be(17L);
        else value.Should().Be(DBNull.Value, "SQL NULL must survive the actual writer, not just DTO projection");
    }

    private static (DirectAgentChatRuntime Runtime, DirectChatTurnRequest Request) CreateRuntime(string scenario)
    {
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(new Provider(scenario))
        { UsageUpdateKind = scenario == "cumulative-partial-error" ? AiUsageUpdateKind.Cumulative : AiUsageUpdateKind.Incremental };
        var tool = AIFunctionFactory.Create(() => new DirectToolResult(ToolCallStatus.Completed, "ok",
            JsonSerializer.SerializeToElement("ok"), "ok"), new AIFunctionFactoryOptions
            { Name = "probe", MarshalResult = (value, _, _) => ValueTask.FromResult(value) });
        var lease = new DirectTurnCapabilityLease([], [new DirectToolBinding(tool, new("probe", ToolCallKind.Other))],
            new Dictionary<Guid, string>(), []);
        return (DirectAgentChatRuntimeTests.CreateRuntime(factory, new Resolver(lease)),
            (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id));
    }

    private sealed class Resolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    private sealed class Provider(string scenario) : IChatClient
    {
        private int _requests;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = ++_requests;
            if (scenario != "cumulative-partial-error")
            {
                yield return Update(new UsageContent(new UsageDetails { TotalTokenCount = 10 }));
                yield return Update(new UsageContent(new UsageDetails { InputTokenCount = 3,
                    OutputTokenCount = scenario == "incremental-complete" ? 4 : null }));
                yield return new ChatResponseUpdate(ChatRole.Assistant, scenario == "incremental-complete"
                    ? [new TextContent("done")]
                    : [new UsageContent(new UsageDetails { OutputTokenCount = 4 }), new TextContent("done")]) { FinishReason = ChatFinishReason.Stop };
            }
            else if (request == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 }),
                     new FunctionCallContent("call", "probe", new Dictionary<string, object?>())]) { FinishReason = ChatFinishReason.ToolCalls };
            }
            else
            {
                yield return Update(new UsageContent(new UsageDetails { InputTokenCount = 7 }));
                throw new IOException("response ended after partial usage");
            }
            await Task.CompletedTask;
        }
        private static ChatResponseUpdate Update(AIContent content) => new(ChatRole.Assistant, [content]);
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
