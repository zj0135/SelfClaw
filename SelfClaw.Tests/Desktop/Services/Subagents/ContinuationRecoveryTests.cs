using SelfClaw.Tests.Infrastructure.Agents.Direct;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Tools.Workspace;
using SelfClaw.Tests.Infrastructure.Agents.Runtime;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class ContinuationRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("before-checkpoint", false)]
    [InlineData("after-checkpoint", true)]
    [InlineData("after-tool", true)]
    public async Task Interrupted_real_function_pipeline_recovers_from_durable_checkpoint(string stopAt, bool unsafeToReplay)
    {
        var database = new SqliteDatabase(StoragePathDefaults.Create(_root, Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets")));
        var conversations = new SqliteConversationRepository(database);
        var tasks = new SqliteSubagentTaskRepository(database, new SubagentCompletionEnvelopeFactory());
        var deliveries = new SqliteSubagentDeliveryRepository(database);
        await tasks.InitializeAsync();
        var task = await SubagentTaskTestData.CreateRunningTaskAsync(conversations, tasks);
        var now = DateTimeOffset.UtcNow;
        await tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Running, new SubagentTaskCompletion(
            SubagentTaskStatus.Succeeded, new TurnFinalization(new MessageRecord(task.ChildTurnId, task.ChildConversationId,
                MessageRole.Assistant, "result", MessageStatus.Completed, now, now), []), "result", null, null, now));
        var mailbox = await deliveries.PeekReadyMailboxAsync(now.AddSeconds(3), now.AddSeconds(3))
            ?? throw new InvalidOperationException("Missing mailbox.");
        var lease = await deliveries.TryLeaseBatchAsync(mailbox, Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(3), now.AddSeconds(45), 65536)
            ?? throw new InvalidOperationException("Missing lease.");
        var parent = await conversations.GetConversationAsync(task.ParentConversationId)
            ?? throw new InvalidOperationException("Missing parent.");
        using var state = new ConversationRuntimeState(parent, [], [], isDetached: true);
        var committer = new SubagentContinuationTurnCommitter(deliveries, lease, TimeProvider.System);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoint = new PausingCheckpoint(committer, stopAt, reached);
        var client = new SingleToolChatClient("write_file", new Dictionary<string, object?>
            { ["relativePath"] = "effect.txt", ["content"] = "executed" },
            async token =>
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(
            new ChatClientBuilder(client).UseFunctionInvocation().Build());
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory,
            DirectAgentChatRuntimeTests.CreateCapabilityResolver(new WorkspaceToolService(new(), new(), new())));
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id,
            new WorkspaceRoot(Guid.NewGuid(), "Workspace", _root, now, now));
        request = new DirectChatTurnRequest(lease.ContinuationTurnId, parent.Id, request.WorkspaceRoot, request.Agent,
            request.Messages, factory.Profile.Id, request.ToolPermissionMode, null,
            new(DirectTurnOrigin.Continuation, new DirectCapabilityCeiling("system", [], [], [], []), null),
            ToolExecutionCheckpoint: checkpoint);
        var recorder = new ConversationTurnRecorder(conversations, NullLogger<ConversationTurnRecorder>.Instance);
        var turn = new AgentTurnState(lease.ContinuationTurnId, request.Agent);
        recorder.BeginTurn(state, turn);
        using var cancellation = new CancellationTokenSource();
        var recording = RecordUntilInterruptedAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await conversations.ListMessagesAsync(parent.Id)).Should().BeEmpty();
        (await conversations.ListToolExecutionsAsync(parent.Id)).Should().BeEmpty();
        (await deliveries.GetAsync(parent.Id, task.Id))!.ToolExecutionStartedAtUtc.HasValue.Should().Be(unsafeToReplay);
        if (stopAt != "after-tool")
        {
            client.ToolResult.Should().BeNull("the checkpoint must finish before the SDK invokes the function");
        }
        File.Exists(Path.Combine(_root, "effect.txt")).Should().Be(stopAt == "after-tool");

        cancellation.Cancel();
        Func<Task> interrupted = () => recording;
        await interrupted.Should().ThrowAsync<OperationCanceledException>();
        var recovered = await deliveries.RecoverExpiredLeasesAsync(now.AddMinutes(1));
        recovered.Count.Should().Be(unsafeToReplay ? 1 : 0);
        (await deliveries.PeekReadyMailboxAsync(now.AddMinutes(2), now.AddMinutes(2)) is null).Should().Be(unsafeToReplay);

        async Task RecordUntilInterruptedAsync()
        {
            await foreach (var item in runtime.StreamTurnAsync(request, cancellation.Token))
            {
                await recorder.ApplyDetachedEventAsync(state, turn, item, committer, cancellation.Token);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class PausingCheckpoint(IToolExecutionCheckpoint inner, string stopAt, TaskCompletionSource reached)
        : IToolExecutionCheckpoint
    {
        public async Task BeforeExecutionAsync(CancellationToken cancellationToken)
        {
            if (stopAt == "before-checkpoint") await PauseAsync(cancellationToken);
            await inner.BeforeExecutionAsync(cancellationToken);
            if (stopAt == "after-checkpoint") await PauseAsync(cancellationToken);
        }

        private async Task PauseAsync(CancellationToken cancellationToken)
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
