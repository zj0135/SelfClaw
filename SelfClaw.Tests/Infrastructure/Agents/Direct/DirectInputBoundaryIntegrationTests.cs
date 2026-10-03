using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

public sealed class DirectInputBoundaryIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task One_turn_interleaves_real_provider_output_tools_and_committed_user_messages()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var provider = new BoundaryProvider();
        var observedInputs = new ObservedInputs(context.InputSession);
        var runtime = CreateRuntime(context, provider, observedInputs, out var request);
        await RecordAsync(runtime, request, context, context.Recorder, CancellationToken.None).WaitAsync(Timeout);
        var messages = await context.Conversations.ListMessagesAsync(context.Conversation.Id);
        messages.Select(message => message.Role).Should().Equal(MessageRole.User, MessageRole.Assistant, MessageRole.User, MessageRole.Assistant);
        messages.Select(message => message.MarkdownContent).Should().Equal("U0", "A0 ", "U1", "A1 done");
        messages.Select(message => message.Id).Should().OnlyHaveUniqueItems().And.NotContain(context.Record.Id);
        messages.Should().OnlyContain(message => message.TurnId == context.Record.Id);
        messages.Select(message => message.Sequence).Should().BeInAscendingOrder();
        var tools = await context.Conversations.ListToolExecutionsAsync(context.Conversation.Id);
        tools.Where(tool => tool.MessageId == messages[1].Id).Select(tool => tool.CorrelationId).Should().BeEquivalentTo("a", "b");
        tools.Where(tool => tool.MessageId == messages[3].Id).Select(tool => tool.CorrelationId).Should().Equal("c");
        (messages[1].Segments ?? []).Select(segment => segment.Kind).Should().Equal(
            MessageSegmentKind.Thinking, MessageSegmentKind.Text, MessageSegmentKind.ToolCall, MessageSegmentKind.ToolCall);
        provider.Requests.Should().HaveCount(3);
        provider.Requests[1].Where(message => message.Role == ChatRole.User).Select(message => message.Text).Should().Equal("U0", "U1");
        var replay = provider.Requests[1].SelectMany(message => message.Contents).ToArray();
        replay.OfType<TextReasoningContent>().Should().ContainSingle().Which.ProtectedData.Should().Be("signature-A0");
        replay.OfType<FunctionResultContent>().Select(result => result.CallId).Should().Equal("a", "b");
        var turn = (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single();
        turn.Status.Should().Be(ConversationTurnStatus.Succeeded);
        var usage = turn.Usage ?? throw new InvalidOperationException("Missing usage.");
        usage.InputTokens.Should().Be(60);
        usage.OutputTokens.Should().Be(12);
        usage.TotalTokens.Should().Be(72);
        usage.ProviderCalls.Should().Be(3);
        (await context.ScalarAsync("SELECT COUNT(*) FROM turn_usage")).Should().Be(1L);
    }

    [Fact]
    public async Task Multiple_claims_before_first_response_are_committed_before_the_first_provider_request()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var originalSequence = context.State.Messages.Single().Sequence;
        await context.SeedClaimAsync("before-one");
        await context.SeedClaimAsync("before-two");
        var provider = new BoundaryProvider();
        var runtime = CreateRuntime(context, provider, new ObservedInputs(context.InputSession), out var request);
        await RecordAsync(runtime, request, context, context.Recorder, CancellationToken.None).WaitAsync(Timeout);
        provider.Requests[0].Where(message => message.Role == ChatRole.User).Select(message => message.Text)
            .Should().Equal("U0", "before-one", "before-two");
        provider.Requests[0].Should().NotContain(message => message.Role == ChatRole.Assistant);
        var messages = await context.Conversations.ListMessagesAsync(context.Conversation.Id);
        messages.Take(3).Should().OnlyContain(message => message.Role == MessageRole.User);
        messages[0].Sequence.Should().Be(originalSequence);
        messages.Select(message => message.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        messages.Count(message => message.Role == MessageRole.Assistant).Should().Be(2);
    }

    [Fact]
    public async Task Previous_tool_result_reduction_blocks_the_next_real_provider_request()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var progress = new GatedProgress(context.Turns);
        var recorder = context.CreateRecorder(turns: progress);
        var provider = new BoundaryProvider();
        var observedInputs = new ObservedInputs(context.InputSession);
        var runtime = CreateRuntime(context, provider, observedInputs, out var request);
        var recording = RecordAsync(runtime, request, context, recorder, CancellationToken.None);
        await progress.Entered.Task.WaitAsync(Timeout);
        var pending = await observedInputs.WaitStarted.Task.WaitAsync(Timeout);
        pending.IsCompleted.Should().BeFalse();
        provider.Requests.Should().HaveCount(1, "the producer reached its incomplete boundary await while the consumer is reducing a prior tool result");
        (await context.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 2")).Should().Be(0L);
        progress.Release.TrySetResult();
        await recording.WaitAsync(Timeout);
        provider.Requests.Should().HaveCount(3);
        context.InputSession.PendingWaiterCount.Should().Be(0);
    }

    [Fact]
    public async Task Actual_sqlite_commit_is_a_barrier_before_consumption_and_next_provider_request()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputs = new SqliteConversationInputRepository(context.Database, async (stage, token) =>
        {
            stage.Should().Be("boundary-before-commit");
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var recorder = context.CreateRecorder(inputs: inputs);
        var provider = new BoundaryProvider();
        var observedInputs = new ObservedInputs(context.InputSession);
        var runtime = CreateRuntime(context, provider, observedInputs, out var request);
        var recording = RecordAsync(runtime, request, context, recorder, CancellationToken.None);
        await entered.Task.WaitAsync(Timeout);
        var pending = await observedInputs.WaitStarted.Task.WaitAsync(Timeout);
        pending.IsCompleted.Should().BeFalse();
        provider.Requests.Should().HaveCount(1);
        (await context.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 2")).Should().Be(0L);
        (await context.ScalarAsync("SELECT COUNT(*) FROM turn_usage")).Should().Be(0L);
        (await context.Conversations.ListMessagesAsync(context.Conversation.Id)).Where(message => message.Role == MessageRole.User)
            .Should().ContainSingle();
        release.TrySetResult();
        await recording.WaitAsync(Timeout);
        provider.Requests.Should().HaveCount(3);
        (await context.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 2")).Should().Be(1L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_or_consumer_failure_during_commit_releases_provider_and_preserves_unconsumed_input(bool fail)
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputs = new SqliteConversationInputRepository(context.Database, async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new IOException("commit fixture failed");
        });
        var provider = new BoundaryProvider();
        var observedInputs = new ObservedInputs(context.InputSession);
        var runtime = CreateRuntime(context, provider, observedInputs, out var request);
        var recording = RecordAsync(runtime, request, context, context.CreateRecorder(inputs: inputs), cancellation.Token);
        await entered.Task.WaitAsync(Timeout);
        await observedInputs.WaitStarted.Task.WaitAsync(Timeout);
        if (fail) release.TrySetResult(); else cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => recording.WaitAsync(Timeout));
        if (fail) error.Should().BeOfType<IOException>();
        else error.Should().BeAssignableTo<OperationCanceledException>();
        provider.Requests.Should().HaveCount(1);
        provider.Disposed.Should().BeTrue();
        context.InputSession.PendingWaiterCount.Should().Be(0);
        (await context.ScalarAsync("SELECT status FROM conversation_inputs")).Should().Be(1L);
        (await context.ScalarAsync("SELECT COUNT(*) FROM turn_usage")).Should().Be(0L);
    }

    private static DirectAgentChatRuntime CreateRuntime(ConversationTurnTestContext context, BoundaryProvider provider,
        IDirectTurnInputSession inputs, out DirectChatTurnRequest request)
    {
        var calls = 0;
        var function = AIFunctionFactory.Create(async () =>
        {
            if (Interlocked.Increment(ref calls) == 1) await context.SeedClaimAsync("U1");
            return new DirectToolResult(ToolCallStatus.Completed, "ok", JsonSerializer.SerializeToElement("ok"), "ok");
        }, new AIFunctionFactoryOptions { Name = "probe", MarshalResult = (value, _, _) => ValueTask.FromResult(value) });
        var lease = new DirectTurnCapabilityLease([], [new DirectToolBinding(function, new("probe", ToolCallKind.Other))],
            new Dictionary<Guid, string>(), []);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider);
        request = new DirectChatTurnRequest(context.Record.Id, context.Conversation.Id, null, context.Agent,
            context.State.Messages, context.State.Turns, factory.Profile.Id, ToolPermissionMode.RequireApproval, null,
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null), InputSession: inputs);
        return DirectAgentChatRuntimeTests.CreateRuntime(factory, new Resolver(lease));
    }

    private static async Task RecordAsync(DirectAgentChatRuntime runtime, DirectChatTurnRequest request,
        ConversationTurnTestContext context, ConversationTurnRecorder recorder, CancellationToken cancellationToken)
    {
        var terminals = 0;
        var usageReports = 0;
        await foreach (var item in runtime.StreamTurnAsync(request, cancellationToken))
        {
            if (item is RunCompletedEvent) terminals++;
            if (item is UsageReportedEvent) usageReports++;
            await recorder.ApplyEventAsync(context.State, context.Turn, item, context.Finalizer, cancellationToken);
        }
        terminals.Should().Be(1);
        usageReports.Should().Be(1);
    }

    private sealed class Resolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    private sealed class ObservedInputs(IDirectTurnInputSession inner) : IDirectTurnInputSession
    {
        internal TaskCompletionSource<Task<ConversationInputConsumption>> WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConversationInputBatch?> ReadBoundaryAsync(CancellationToken cancellationToken = default)
            => inner.ReadBoundaryAsync(cancellationToken);
        public Task<ConversationInputConsumption> WaitForCommitAsync(ConversationInputBatch batch, CancellationToken cancellationToken = default)
        {
            var pending = inner.WaitForCommitAsync(batch, cancellationToken);
            WaitStarted.TrySetResult(pending);
            return pending;
        }
    }

    private sealed class GatedProgress(IConversationTurnRepository inner) : IConversationTurnRepository
    {
        private bool _blocked;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task CommitProgressAsync(ConversationTurnCommit progress, CancellationToken cancellationToken = default)
        {
            if (!_blocked && progress.ToolExecutions.Any(tool => tool.Status == ToolExecutionStatus.Completed))
            {
                _blocked = true;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await inner.CommitProgressAsync(progress, cancellationToken);
        }
        public Task<ConversationTurnCommit> StartTurnAsync(ConversationTurnStart start, CancellationToken cancellationToken = default)
            => inner.StartTurnAsync(start, cancellationToken);
        public Task<long> ReserveMessageSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => inner.ReserveMessageSequenceAsync(conversationId, cancellationToken);
        public Task<IReadOnlyList<ConversationTurnRecord>> ListTurnsAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => inner.ListTurnsAsync(conversationId, cancellationToken);
        public Task<bool> TryFinalizeTurnAsync(ConversationTurnCommit commit, CancellationToken cancellationToken = default)
            => inner.TryFinalizeTurnAsync(commit, cancellationToken);
    }

    private sealed class BoundaryProvider : IChatClient
    {
        internal List<ChatMessage[]> Requests { get; } = [];
        internal bool Disposed { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            var request = Requests.Count;
            if (request == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("think A0") { ProtectedData = "signature-A0" }, new TextContent("A0 ")]);
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("a", "probe", new Dictionary<string, object?>()), new FunctionCallContent("b", "probe", new Dictionary<string, object?>())]);
            }
            else if (request == 2)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("A1 "), new FunctionCallContent("c", "probe", new Dictionary<string, object?>())]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = request * 10, OutputTokenCount = request * 2, TotalTokenCount = request * 12 })])
            { FinishReason = request < 3 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop };
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
}
