using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

/// <summary>
/// P4.2/P4.5/P4.7 at the loop boundary: a steer never cancels the provider stream, never starts an
/// unstarted tool, keeps its claim order, and a batch that cannot fit the budget is held instead of
/// being silently trimmed.
/// </summary>
public sealed class DirectSteerLoopIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_steer_during_streaming_is_consumed_after_the_tool_unit_in_the_same_turn()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var provider = new SteerProvider { GateFirstStream = true };
        var runtime = CreateRuntime(context, provider, out var request);
        var recording = RecordAsync(runtime, request, context);
        await provider.FirstStreamStarted.Task.WaitAsync(Timeout);
        await context.SeedPendingSteerAsync("U1");
        provider.ReleaseFirstStream.TrySetResult();

        var result = await recording.WaitAsync(Timeout);

        result.Terminals.Should().Be(1);
        result.UsageReports.Should().Be(1);
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Where(message => message.Role == ChatRole.User).Select(message => message.Text)
            .Should().Equal("U0", "U1");
        provider.Requests[1].SelectMany(message => message.Contents).OfType<TextReasoningContent>()
            .Should().ContainSingle().Which.ProtectedData.Should().Be("signature-A0");
        provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(item => item.CallId).Should().Equal("p");
        var messages = await context.Conversations.ListMessagesAsync(context.Conversation.Id);
        messages.Select(message => message.MarkdownContent).Should().Equal("U0", "A0 ", "U1", "done");
        messages.Should().OnlyContain(message => message.TurnId == context.Record.Id);
        (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single().Status.Should().Be(ConversationTurnStatus.Succeeded);
    }

    [Fact]
    public async Task Only_the_executing_tool_finishes_and_unstarted_calls_become_superseded_results_before_the_steer()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var executions = new List<string>();
        var toolAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SteerProvider { ToolNames = ["a", "b", "c"] };
        var steering = new PostClaimSeedSteering(context.InputSession, () => context.SeedPendingSteerAsync("U3"));
        var runtime = CreateRuntime(context, provider, out var request, tool => tool switch
        {
            "a" => async () =>
            {
                executions.Add("a");
                toolAStarted.TrySetResult();
                await releaseA.Task.WaitAsync(Timeout);
                return (object?)Result("a");
            },
            _ => () =>
            {
                executions.Add(tool);
                return Task.FromResult<object?>(Result(tool));
            }
        }, steering);
        var recording = RecordAsync(runtime, request, context);
        await toolAStarted.Task.WaitAsync(Timeout);
        await context.SeedPendingSteerAsync("U1", "U2");
        releaseA.TrySetResult();

        var result = await recording.WaitAsync(Timeout);

        executions.Should().Equal("a");
        var toolResults = provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
        toolResults.Select(item => item.CallId).Should().Equal("a", "b", "c");
        toolResults.Select(item => ((DirectToolResult)item.Result!).Status)
            .Should().Equal(ToolCallStatus.Completed, ToolCallStatus.Canceled, ToolCallStatus.Canceled);
        toolResults.Select(item => ((DirectToolResult)item.Result!).Detail)
            .Should().Equal("a", DirectToolInvoker.SupersededBySteerReason, DirectToolInvoker.SupersededBySteerReason);
        provider.Requests[1].Where(message => message.Role == ChatRole.User).Select(message => message.Text)
            .Should().Equal("U0", "U1", "U2", "U3");
        result.Boundaries.Should().Be(2, "the later steer waits for its own boundary instead of extending the claimed batch");
        result.Terminals.Should().Be(1);
        (await context.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 2;")).Should().Be(3L);
        (await context.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status <> 2;")).Should().Be(0L);
    }

    [Fact]
    public async Task A_pending_steer_without_new_tools_extends_the_same_turn_instead_of_ending_it()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var provider = new SteerProvider { ToolNames = [] };
        var runtime = CreateRuntime(context, provider, out var request);
        await context.SeedPendingSteerAsync("U1");

        var result = await RecordAsync(runtime, request, context).WaitAsync(Timeout);

        result.Terminals.Should().Be(1);
        provider.Requests.Should().ContainSingle("the pre-request boundary consumes the steer before the first provider call");
        provider.Requests[0].Where(message => message.Role == ChatRole.User).Select(message => message.Text)
            .Should().Equal("U0", "U1");
        (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single().Id.Should().Be(context.Record.Id);
    }

    [Fact]
    public async Task A_steer_does_not_reset_the_128_request_budget()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var executions = 0;
        var provider = new SteerProvider { ToolNames = ["p"], Endless = true };
        var runtime = CreateRuntime(context, provider, out var request, _ => () =>
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult<object?>(Result("ok"));
        });
        await context.SeedPendingSteerAsync("U1");

        var result = await RecordAsync(runtime, request, context).WaitAsync(Timeout);

        result.Terminal!.Status.Should().Be(RunCompletionStatus.Failed);
        result.Terminal.ErrorMessage.Should().Contain("BudgetExhausted");
        provider.Requests.Should().HaveCount(128, "a consumed steer does not grant another request");
        executions.Should().Be(127);
        (await context.ScalarAsync("SELECT status FROM conversation_inputs;")).Should().Be(2L);
    }

    [Fact]
    public async Task A_steer_batch_that_cannot_fit_the_context_budget_is_held_without_an_extra_provider_request()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var provider = new SteerProvider { ToolNames = [] };
        var runtime = CreateRuntime(context, provider, out var request, contextWindowTokens: 4200);
        await context.SeedPendingSteerAsync(new string('x', 20_000));

        var result = await RecordAsync(runtime, request, context).WaitAsync(Timeout);

        result.Terminals.Should().Be(1);
        result.Terminal!.ErrorMessage.Should().Contain("ContextBudgetExceeded");
        provider.Requests.Should().BeEmpty("the budget is checked before any provider request masks the failure");
        (await context.ScalarAsync("SELECT status FROM conversation_inputs;")).Should().Be(3L);
        (await context.ScalarAsync("SELECT reason_code FROM conversation_inputs;"))
            .Should().Be(ConversationInputReason.ContextBudgetExceeded);
        (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single().Status.Should().Be(ConversationTurnStatus.Failed);
    }

    [Fact]
    public async Task A_steer_accepted_during_the_final_close_check_is_still_consumed_this_turn()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        ConversationInputRecord? accepted = null;
        var provider = new SteerProvider { ToolNames = [] };
        var session = new AcceptOnSecondEmptyInputSession(context.InputSession, async () =>
            accepted = await AcceptSteerAsync(context, "after-empty-check"));
        var runtime = CreateRuntime(context, provider, out var request, inputSession: session);

        var result = await RecordAsync(runtime, request, context).WaitAsync(Timeout);

        result.Terminals.Should().Be(1);
        provider.Requests.Should().HaveCount(2, "an accept before the atomic close must still join this turn");
        provider.Requests[1].Where(message => message.Role == ChatRole.User).Select(message => message.Text)
            .Should().Equal("U0", "after-empty-check");
        var stored = (await context.Inputs.GetInputAsync(accepted!.Id))!;
        stored.Status.Should().Be(ConversationInputStatus.Consumed);
        stored.Kind.Should().Be(ConversationInputKind.Steer);
        stored.ConsumedTurnId.Should().Be(context.Record.Id);
    }

    [Fact]
    public async Task An_explicit_skill_token_steer_is_not_injected_and_waits_as_a_follow_up()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var steer = await AcceptSteerAsync(context, "please [/pdf] now");
        var provider = new SteerProvider { ToolNames = [] };
        var runtime = CreateRuntime(context, provider, out var request);

        var result = await RecordAsync(runtime, request, context).WaitAsync(Timeout);

        result.Terminals.Should().Be(1);
        provider.Requests.Should().HaveCount(1);
        provider.Requests[0].SelectMany(message => message.Contents).OfType<TextContent>()
            .Select(text => text.Text).Should().NotContain(text => text.Contains("[/pdf]"));
        var stored = (await context.Inputs.GetInputAsync(steer.Id))!;
        stored.Kind.Should().Be(ConversationInputKind.FollowUp);
        stored.ReasonCode.Should().Be(ConversationInputReason.SkillRequiresNewTurn);
        stored.Status.Should().Be(ConversationInputStatus.Pending);
    }

    private static async Task<ConversationInputRecord> AcceptSteerAsync(ConversationTurnTestContext context, string prompt)
    {
        var result = await context.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            context.Conversation.Id, Guid.NewGuid().ToString("N"), ConversationInputKind.Steer, context.Record.Id, prompt,
            new ConversationInputExecutionSnapshot("build", 1, AgentExecutionMode.Direct, Guid.NewGuid(), null, null,
                ToolPermissionMode.RequireApproval), null));
        result.Status.Should().Be(ConversationInputAcceptStatus.Accepted, result.Reason);
        return result.Input!;
    }

    /// <summary>Accepts a steer exactly after the second empty plain boundary read, before the loop's atomic close.</summary>
    private sealed class AcceptOnSecondEmptyInputSession(DirectTurnInputSession inner, Func<Task> accept) : IDirectTurnInputSession
    {
        private int _empties;

        public async Task<ConversationInputBatch?> ReadBoundaryAsync(CancellationToken cancellationToken = default)
        {
            var batch = await inner.ReadBoundaryAsync(cancellationToken);
            if (batch is null && Interlocked.Increment(ref _empties) == 2)
            {
                await accept();
            }

            return batch;
        }

        public Task<ConversationInputBatch?> ReadFinalBoundaryAsync(CancellationToken cancellationToken = default)
            => inner.ReadFinalBoundaryAsync(cancellationToken);

        public Task<ConversationInputConsumption> WaitForCommitAsync(ConversationInputBatch batch, CancellationToken cancellationToken = default)
            => inner.WaitForCommitAsync(batch, cancellationToken);

        public Task HoldUnconsumedAsync(string reasonCode, CancellationToken cancellationToken = default)
            => inner.HoldUnconsumedAsync(reasonCode, cancellationToken);
    }

    private static DirectToolResult Result(string value)
        => new(ToolCallStatus.Completed, value, System.Text.Json.JsonSerializer.SerializeToElement(value), value);

    private static DirectAgentChatRuntime CreateRuntime(ConversationTurnTestContext context, SteerProvider provider,
        out DirectChatTurnRequest request, Func<string, Func<Task<object?>>>? tools = null,
        IDirectTurnSteering? steering = null, int? contextWindowTokens = null,
        IDirectTurnInputSession? inputSession = null)
    {
        var session = inputSession ?? context.InputSession;
        var bindings = provider.ToolNames
            .Select(name =>
            {
                var invoke = tools?.Invoke(name) ?? (() => Task.FromResult<object?>(name));
                var function = AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions
                {
                    Name = name,
                    Description = $"Synthetic tool {name}.",
                    ExcludeResultSchema = true,
                    MarshalResult = (value, _, _) => ValueTask.FromResult(value)
                });
                return new DirectToolBinding(function, new(name, ToolCallKind.Other));
            })
            .ToArray();
        var lease = new DirectTurnCapabilityLease([], bindings, new Dictionary<Guid, string>(), []);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider, contextWindowTokens);
        request = new DirectChatTurnRequest(context.Record.Id, context.Conversation.Id, null, context.Agent,
            context.State.Messages, context.State.Turns, factory.Profile.Id, ToolPermissionMode.RequireApproval, null,
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null), InputSession: session,
            Steering: steering ?? context.InputSession);
        return DirectAgentChatRuntimeTests.CreateRuntime(factory, new FixedResolver(lease));
    }

    private static async Task<RecordingResult> RecordAsync(DirectAgentChatRuntime runtime, DirectChatTurnRequest request,
        ConversationTurnTestContext context, CancellationToken cancellationToken = default)
    {
        var terminals = 0;
        var usageReports = 0;
        var boundaries = 0;
        RunCompletedEvent? terminal = null;
        await foreach (var item in runtime.StreamTurnAsync(request, cancellationToken))
        {
            if (item is RunCompletedEvent completed) { terminals++; terminal = completed; }
            if (item is UsageReportedEvent) usageReports++;
            if (item is TurnInputBoundaryEvent) boundaries++;
            await context.Recorder.ApplyEventAsync(context.State, context.Turn, item, context.Finalizer, cancellationToken);
        }

        return new RecordingResult(terminals, usageReports, boundaries, terminal);
    }

    private sealed record RecordingResult(int Terminals, int UsageReports, int Boundaries, RunCompletedEvent? Terminal);

    private sealed class FixedResolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    /// <summary>Seeds a later steer exactly after the first durable claim, before the loop reaches its boundary.</summary>
    private sealed class PostClaimSeedSteering(DirectTurnInputSession inner, Func<Task> afterFirstClaim) : IDirectTurnSteering
    {
        private int _claimed;

        public string? SteerBlockReason => inner.SteerBlockReason;
        public bool HasSupersedingSteer => inner.HasSupersedingSteer;

        public async Task<bool> TryBeginToolExecutionAsync(CancellationToken cancellationToken = default)
        {
            var admitted = await inner.TryBeginToolExecutionAsync(cancellationToken);
            if (!admitted && Interlocked.Exchange(ref _claimed, 1) == 0)
            {
                await afterFirstClaim();
            }

            return admitted;
        }

        public Task WaitForSteerPulseAsync(CancellationToken cancellationToken = default)
            => inner.WaitForSteerPulseAsync(cancellationToken);

        public Task SetSteerPolicyAsync(string? blockReason, CancellationToken cancellationToken = default)
            => inner.SetSteerPolicyAsync(blockReason, cancellationToken);
    }

    private sealed class SteerProvider : IChatClient
    {
        internal List<ChatMessage[]> Requests { get; } = [];
        internal IReadOnlyList<string> ToolNames { get; init; } = ["p"];
        internal bool GateFirstStream { get; init; }
        internal bool Endless { get; init; }
        internal TaskCompletionSource FirstStreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstStream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            var request = Requests.Count;
            if (request == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new TextReasoningContent("think") { ProtectedData = "signature-A0" },
                    new TextContent("A0 ")
                ]);
                if (GateFirstStream)
                {
                    FirstStreamStarted.TrySetResult();
                    await ReleaseFirstStream.Task.WaitAsync(cancellationToken);
                }

                if (ToolNames.Count > 0)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        ToolNames.Select(name => new FunctionCallContent(name, name, new Dictionary<string, object?>())).ToArray())
                    { FinishReason = ChatFinishReason.ToolCalls };
                }
                else
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
                    { FinishReason = ChatFinishReason.Stop };
                }
            }
            else if (Endless)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{request}", ToolNames[0], new Dictionary<string, object?>())])
                { FinishReason = ChatFinishReason.ToolCalls };
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
                { FinishReason = ChatFinishReason.Stop };
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = request * 10, OutputTokenCount = request * 2 })]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
