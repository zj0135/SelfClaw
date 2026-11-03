using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

public sealed class DirectConversationLoopContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incremental_total_only_fragments_survive_the_runtime_usage_report(bool incomplete)
    {
        var provider = new Provider((n, _, _) => Reply(
            Update(n, null, new UsageContent(new UsageDetails { TotalTokenCount = 10 })),
            Update(n, null, new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = incomplete ? null : 4 })),
            Update(n, ChatFinishReason.Stop, new TextContent("done"),
                new UsageContent(incomplete ? new UsageDetails { OutputTokenCount = 4 } : new UsageDetails()))));
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider) { UsageUpdateKind = AiUsageUpdateKind.Incremental };
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new Resolver(Lease(Tool(Success))));
        var events = await CollectAsync(runtime, (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id));
        var usage = events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
        usage.TotalTokens.Should().Be(incomplete ? null : 17);
        usage.ProviderCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("refusal")]
    [InlineData("unknown-finish")]
    [InlineData("error")]
    public async Task Abnormal_response_never_executes_tools_or_requests_a_followup(string scenario)
    {
        var executions = 0;
        var checkpoint = new CountingCheckpoint();
        var provider = new Provider((n, _, _) => n == 1
            ? Reply(Update(n, scenario switch
                {
                    "filter" => ChatFinishReason.ContentFilter,
                    "unknown-finish" => new ChatFinishReason("provider_error"),
                    _ => ChatFinishReason.Stop
                }, new TextContent("partial"), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 })),
                Update(n, null, Call("a"), Call("b")),
                Update(n, null, scenario == "refusal" ? new ErrorContent("refused") { ErrorCode = "Refusal" }
                    : scenario == "error" ? new ErrorContent("provider failed") : new TextContent("tail")))
            : Reply(Update(n, ChatFinishReason.Stop, new TextContent("should not be requested"))));
        var (runtime, request) = Runtime(provider, Tool(() => { executions++; return Success(); }));
        var events = await CollectAsync(runtime, request with { ToolExecutionCheckpoint = checkpoint });
        executions.Should().Be(0);
        checkpoint.Calls.Should().Be(0);
        provider.Requests.Should().Be(1);
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Subject;
        terminal.Status.Should().Be(scenario is "filter" or "refusal" ? RunCompletionStatus.Blocked : RunCompletionStatus.Failed);
        terminal.FinalText.Should().StartWith("partial");
        if (scenario is "filter" or "refusal")
        {
            var results = events.OfType<ToolCallCompletedEvent>().ToArray();
            results.Select(result => result.ToolCallId).Should().Equal("a", "b");
            results.Should().OnlyContain(result => result.Status == ToolCallStatus.Canceled && result.ResultSummary == "ProviderContentFilter");
        }
        if (scenario == "refusal") terminal.FinalText.Should().Be("partialrefused");
        var usage = events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
        usage.InputTokens.Should().Be(10);
        usage.OutputTokens.Should().Be(2);
        usage.ProviderCalls.Should().Be(1);
        provider.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("empty", false)]
    [InlineData("usage", false)]
    [InlineData("metadata", false)]
    [InlineData("empty-text", false)]
    [InlineData("empty", true)]
    [InlineData("usage", true)]
    public async Task No_content_or_completion_is_a_failure_even_after_a_successful_tool_batch(string scenario, bool afterTool)
    {
        var provider = new Provider((n, _, _) => afterTool && n == 1
            ? Reply(Update(n, ChatFinishReason.ToolCalls, Call("a"), new TextContent("earlier")))
            : scenario switch
            {
                "empty" => Reply(),
                "usage" => Reply(Update(n, null, new UsageContent(new UsageDetails { InputTokenCount = 7 }))),
                "metadata" => Reply(new ChatResponseUpdate { ResponseId = "response", ModelId = "test-model" }),
                _ => Reply(Update(n, null, new TextContent(""), new TextReasoningContent("")))
            });
        var (runtime, request) = Runtime(provider, Tool(Success));
        var events = await CollectAsync(runtime, request);
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Subject;
        terminal.Status.Should().Be(RunCompletionStatus.Failed);
        terminal.ErrorMessage.Should().Contain("no response content or completion");
        provider.Requests.Should().Be(afterTool ? 2 : 1);
        if (scenario == "usage") events.OfType<UsageReportedEvent>().Should().ContainSingle().Which.Usage.InputTokens.Should().Be(7);
        else events.OfType<UsageReportedEvent>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("usage-stop")]
    [InlineData("text")]
    [InlineData("reasoning")]
    [InlineData("protected-reasoning")]
    [InlineData("image")]
    public async Task Explicit_completion_or_supported_response_content_can_succeed_without_body_text(string scenario)
    {
        var provider = new Provider((n, _, _) => Reply(scenario switch
        {
            "stop" => Update(n, ChatFinishReason.Stop),
            "usage-stop" => Update(n, ChatFinishReason.Stop, new UsageContent(new UsageDetails { InputTokenCount = 7 })),
            "text" => Update(n, null, new TextContent("answer")),
            "reasoning" => Update(n, null, new TextReasoningContent("thinking")),
            "protected-reasoning" => Update(n, null, new TextReasoningContent("") { ProtectedData = "signature" }),
            _ => Update(n, null, new DataContent(new byte[] { 1, 2, 3 }, "image/png"))
        }));
        var (runtime, request) = Runtime(provider, Tool(Success));
        var events = await CollectAsync(runtime, request);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
        provider.Requests.Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Request_limit_closes_all_unexecuted_calls_or_accepts_a_final_answer(bool stop, bool finalAnswer)
    {
        var limit = DirectTurnRun.MaximumProviderRequestsPerTurn;
        var executions = 0;
        var provider = new Provider((n, _, _) => Reply(n == limit && finalAnswer
            ? Update(n, ChatFinishReason.Stop, new TextContent("done"))
            : Update(n, stop ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls, Call($"a-{n}"), Call($"b-{n}"))));
        // A fresh result per execution keeps the no-progress guard out of a budget test.
        var (runtime, request) = Runtime(provider, Tool(() => SuccessAt(++executions)));
        var events = await CollectAsync(runtime, request);

        provider.Requests.Should().Be(limit);
        executions.Should().Be((limit - 1) * 2);
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Subject;
        terminal.Status.Should().Be(finalAnswer ? RunCompletionStatus.Succeeded : RunCompletionStatus.Failed);
        var results = events.OfType<ToolCallCompletedEvent>().ToArray();
        results.Should().HaveCount(finalAnswer ? (limit - 1) * 2 : limit * 2);
        events.OfType<ToolCallStartedEvent>().Select(item => item.ToolCallId).Should().Equal(results.Select(item => item.ToolCallId));
        if (!finalAnswer)
        {
            terminal.ErrorMessage.Should().Contain("BudgetExhausted");
            results.TakeLast(2).Should().OnlyContain(result => result.Status == ToolCallStatus.Canceled && result.ResultSummary == "BudgetExhausted");
        }
        else terminal.FinalText.Should().Be("done");
        provider.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Calls_after_finish_wait_for_response_end_and_execute_once(bool stop)
    {
        var atTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        async IAsyncEnumerable<ChatResponseUpdate> Stream(int n, ChatMessage[] history,
            [EnumeratorCancellation] CancellationToken token)
        {
            if (n == 1)
            {
                yield return Update(n, stop ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls, new TextContent("before"));
                yield return Update(n, null, Call("a"), Call("b"));
                atTail.SetResult();
                await release.Task.WaitAsync(token);
                executions.Should().Be(0, "even a complete call must wait for the end of the response");
                yield return Update(n, null, new TextReasoningContent("tail"));
            }
            else
            {
                history.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .Select(result => result.CallId).Should().Equal("a", "b");
                yield return Update(n, ChatFinishReason.Stop, new TextContent("after"));
            }
        }
        var provider = new Provider(Stream);
        var (runtime, request) = Runtime(provider, Tool(() => { executions++; return Success(); }));
        var run = CollectAsync(runtime, request);
        await atTail.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var events = await run.WaitAsync(TimeSpan.FromSeconds(5));
        executions.Should().Be(2);
        provider.Requests.Should().Be(2);
        events.OfType<AssistantThinkingDeltaEvent>().Should().ContainSingle().Which.Delta.Should().Be("tail");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.FinalText.Should().Be("beforeafter");
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("checkpoint")]
    public async Task Provider_contract_breach_or_checkpoint_failure_executes_no_tools_and_does_not_retry(string scenario)
    {
        var executions = 0;
        var invalid = scenario == "duplicate" ? Call("a") : Call("b");
        var provider = new Provider((n, _, _) => Reply(Update(n, ChatFinishReason.ToolCalls, Call("a"), invalid)));
        var (runtime, request) = Runtime(provider, Tool(() => { executions++; return Success(); }));
        if (scenario == "checkpoint") request = request with { ToolExecutionCheckpoint = new FailingCheckpoint() };
        var events = await CollectAsync(runtime, request);
        executions.Should().Be(0);
        provider.Requests.Should().Be(1);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Failed);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("invalid-arguments")]
    public async Task A_model_side_tool_mistake_becomes_a_failed_result_and_the_turn_continues(string scenario)
    {
        var executions = 0;
        var rejected = scenario == "invalid-arguments"
            ? new FunctionCallContent("b", "probe", null) { Exception = new JsonException("invalid JSON") }
            : new FunctionCallContent("b", "missing", new Dictionary<string, object?>());
        var provider = new Provider((n, _, _) => Reply(n == 1
            ? Update(n, ChatFinishReason.ToolCalls, Call("a"), rejected)
            : Update(n, ChatFinishReason.Stop, new TextContent("recovered"))));
        var (runtime, request) = Runtime(provider, Tool(() => { executions++; return Success(); }));
        var events = await CollectAsync(runtime, request);

        provider.Requests.Should().Be(2, "the model gets the failure as a result and answers again");
        executions.Should().Be(1, "the well-formed call of the same batch still ran");
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Subject;
        terminal.Status.Should().Be(RunCompletionStatus.Succeeded);
        terminal.FinalText.Should().Be("recovered");
        var started = events.OfType<ToolCallStartedEvent>().Select(item => item.ToolCallId).ToArray();
        var completed = events.OfType<ToolCallCompletedEvent>().ToArray();
        started.Should().Equal("a", "b");
        completed.Select(item => item.ToolCallId).Should().Equal(started,
            "each started call has exactly one terminal event");
        completed.Select(item => item.Status).Should().Equal(ToolCallStatus.Completed, ToolCallStatus.Failed);
        var modelResults = provider.Histories[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
        modelResults.Select(item => item.CallId).Should().Equal("a", "b");
        modelResults[1].Result.Should().BeOfType<DirectToolResult>()
            .Which.Status.Should().Be(ToolCallStatus.Failed);
        modelResults[1].Result.Should().BeOfType<DirectToolResult>()
            .Which.Summary.Should().Contain(scenario == "invalid-arguments" ? "could not be parsed" : "not bound");
    }

    [Fact]
    public async Task A_batch_that_keeps_hallucinating_tool_names_still_ends_the_turn_at_the_fault_limit()
    {
        var provider = new Provider((n, _, _) => Reply(Update(n, ChatFinishReason.ToolCalls,
            Unknown("a"), Unknown("b"), Unknown("c"))));
        var (runtime, request) = Runtime(provider, Tool(Success));
        var events = await CollectAsync(runtime, request);

        provider.Requests.Should().Be(1, "the batch spends the whole fault budget before a second request");
        events.OfType<ToolCallStartedEvent>().Select(item => item.ToolCallId).Should().Equal("a", "b", "c");
        events.OfType<ToolCallCompletedEvent>().Should().HaveCount(3,
            "each started call still has exactly one terminal event")
            .And.OnlyContain(item => item.Status == ToolCallStatus.Failed);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Failed);
    }

    [Fact]
    public async Task An_aborted_batch_gives_every_unexecuted_call_a_cancelled_terminal_event()
    {
        // Two single-call batches spend the consecutive-fault budget, so the first call of the third batch
        // aborts the turn and the other two never run.
        var executions = 0;
        var provider = new Provider((n, _, _) => Reply(n switch
        {
            1 => Update(n, ChatFinishReason.ToolCalls, Unknown("x")),
            2 => Update(n, ChatFinishReason.ToolCalls, Unknown("y")),
            _ => Update(n, ChatFinishReason.ToolCalls, Unknown("a"), Call("b"), Call("c"))
        }));
        var (runtime, request) = Runtime(provider, Tool(() => { executions++; return Success(); }));
        var events = await CollectAsync(runtime, request);

        executions.Should().Be(0, "the budget aborted the turn before any bound tool ran");
        provider.Requests.Should().Be(3);
        events.OfType<ToolCallStartedEvent>().Select(item => item.ToolCallId).Should().Equal("x", "y", "a", "b", "c");
        var completed = events.OfType<ToolCallCompletedEvent>().ToArray();
        completed.Select(item => item.ToolCallId).Should().Equal(["x", "y", "a", "b", "c"],
            "the two remaining calls of the aborted batch still get their one terminal event");
        completed.Select(item => item.Status).Should().Equal(ToolCallStatus.Failed, ToolCallStatus.Failed,
            ToolCallStatus.Failed, ToolCallStatus.Canceled, ToolCallStatus.Canceled);
        completed[2].ResultSummary.Should().Contain("missing-a", "the aborting call reports its own fault");
        completed[3].ResultSummary.Should().Be(DirectLoopMessages.BatchAbortedReason);
        completed[4].ResultSummary.Should().Be(DirectLoopMessages.BatchAbortedReason);
        completed[3].ResultContent.Should().Contain("earlier tool call of the same batch");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Failed);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("consumer")]
    public async Task Cancellation_and_abandoned_consumer_release_provider_then_capability(string scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new List<string>();
        async IAsyncEnumerable<ChatResponseUpdate> Stream(int n, ChatMessage[] _,
            [EnumeratorCancellation] CancellationToken token)
        {
            if (scenario == "tool") yield return Update(n, ChatFinishReason.ToolCalls, Call("a"));
            else
            {
                yield return Update(n, null, new TextContent("partial"));
                reached.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        }
        var tool = AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Success();
        }, "probe");
        var provider = new Provider(Stream) { OnDispose = () => disposed.Add("provider") };
        var (runtime, request) = Runtime(provider, tool, () => disposed.Add("capability"));
        if (scenario == "consumer")
        {
            var enumerator = runtime.StreamTurnAsync(request).GetAsyncEnumerator();
            while (await enumerator.MoveNextAsync())
                if (enumerator.Current is AssistantTextDeltaEvent) break;
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await FluentActions.Awaiting(() => enumerator.DisposeAsync().AsTask())
                .Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var run = CollectAsync(runtime, request, cancellation.Token);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await FluentActions.Awaiting(() => run.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().ThrowAsync<OperationCanceledException>();
        }
        disposed.Should().Equal("provider", "capability");
        provider.Requests.Should().Be(1);
    }

    [Theory]
    [InlineData(AiUsageUpdateKind.Incremental)]
    [InlineData(AiUsageUpdateKind.Cumulative)]
    public async Task Usage_normalizes_each_request_before_accumulating_cache_context_and_cost(AiUsageUpdateKind kind)
    {
        var provider = new Provider((n, _, _) => n == 3
            ? Reply(Update(n, ChatFinishReason.Stop, new TextContent("done")))
            : Reply(Update(n, null, new UsageContent(new UsageDetails
                { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = n == 1 ? 12 : null })),
                Update(n, null, new UsageContent(new UsageDetails
                {
                    InputTokenCount = kind == AiUsageUpdateKind.Incremental ? 5 : 15,
                    OutputTokenCount = kind == AiUsageUpdateKind.Incremental ? 4 : 6,
                    TotalTokenCount = n == 1 ? (kind == AiUsageUpdateKind.Incremental ? 9 : 21) : null,
                    CachedInputTokenCount = 3, ReasoningTokenCount = 2,
                    AdditionalCounts = new() { ["CacheCreationInputTokens"] = 2 }
                })), Update(n, ChatFinishReason.ToolCalls, Call($"call-{n}"))));
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider)
        { UsageUpdateKind = kind };
        factory.Profile = factory.Profile with { Configuration = new AiModelConfiguration("test-model", "test",
            new(false, 0, false, 0), PriceInPerMTok: 2, PriceOutPerMTok: 4,
            PriceCacheReadPerMTok: 1, PriceCacheWritePerMTok: 3) };
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new Resolver(Lease(Tool(Success))));
        var events = await CollectAsync(runtime, (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id));
        var usage = events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
        provider.Requests.Should().Be(3);
        usage.ProviderCalls.Should().Be(2);
        usage.InputTokens.Should().Be(30);
        usage.OutputTokens.Should().Be(12);
        usage.TotalTokens.Should().Be(42);
        usage.ReasoningTokens.Should().Be(4);
        usage.CachedInputTokens.Should().Be(6);
        usage.CacheWriteInputTokens.Should().Be(4);
        usage.UncachedInputTokens.Should().Be(20);
        usage.ContextTokens.Should().Be(21);
        usage.CostUsdMicros.Should().Be(106);
        usage.CostSource.Should().Be(TurnUsageCostSource.Estimated);
    }

    [Fact]
    public async Task Cumulative_snapshot_without_total_does_not_keep_a_stale_earlier_total()
    {
        var provider = new Provider((n, _, _) => Reply(
            Update(n, null, new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 })),
            Update(n, ChatFinishReason.Stop, new UsageContent(new UsageDetails { OutputTokenCount = 6 }))));
        var (runtime, request) = Runtime(provider, Tool(Success));
        var events = await CollectAsync(runtime, request);
        var usage = events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
        usage.InputTokens.Should().Be(10);
        usage.OutputTokens.Should().Be(6);
        usage.TotalTokens.Should().Be(16);
        usage.ProviderCalls.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_after_usage_reports_observed_usage_before_propagating_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<ChatResponseUpdate> Stream(int n, ChatMessage[] _, [EnumeratorCancellation] CancellationToken token)
        {
            yield return Update(n, null, new UsageContent(new UsageDetails { InputTokenCount = 9 }));
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        var (runtime, request) = Runtime(new Provider(Stream), Tool(Success));
        var events = new List<AgentStreamEvent>();
        async Task ReadAsync()
        {
            await foreach (var item in runtime.StreamTurnAsync(request, cancellation.Token)) events.Add(item);
        }
        var run = ReadAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await FluentActions.Awaiting(() => run.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<OperationCanceledException>();
        var usage = events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
        usage.ProviderCalls.Should().Be(1);
        usage.InputTokens.Should().Be(9);
        usage.OutputTokens.Should().BeNull();
        usage.TotalTokens.Should().BeNull();
        usage.ContextTokens.Should().BeNull();
        events.OfType<RunCompletedEvent>().Should().BeEmpty("the consuming turn engine owns cancellation finalization");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Length_with_complete_calls_never_executes_them(bool hasText)
    {
        var executions = 0;
        var provider = new Provider((n, _, _) => Reply(Update(n, ChatFinishReason.Length,
            new TextContent(hasText ? "partial" : ""), Call("a"))));
        var (runtime, request) = Runtime(provider, Tool(() => { executions++; return Success(); }));
        var events = await CollectAsync(runtime, request);
        provider.Requests.Should().Be(1);
        executions.Should().Be(0);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status
            .Should().Be(hasText ? RunCompletionStatus.Truncated : RunCompletionStatus.Failed);
    }

    [Fact]
    public async Task Provider_failure_after_usage_preserves_one_observed_request()
    {
        async IAsyncEnumerable<ChatResponseUpdate> Stream(int n, ChatMessage[] _, [EnumeratorCancellation] CancellationToken token)
        {
            yield return Update(n, null, new UsageContent(new UsageDetails { InputTokenCount = 7 }));
            await Task.CompletedTask;
            throw new IOException("stream failed");
        }
        var (runtime, request) = Runtime(new Provider(Stream), Tool(Success));
        var events = await CollectAsync(runtime, request);
        var usage = events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
        usage.InputTokens.Should().Be(7);
        usage.OutputTokens.Should().BeNull();
        usage.TotalTokens.Should().BeNull();
        usage.ContextTokens.Should().BeNull();
        usage.ProviderCalls.Should().Be(1);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Failed);
    }

    private static (DirectAgentChatRuntime, DirectChatTurnRequest) Runtime(Provider provider, AIFunction tool, Action? dispose = null)
    {
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider);
        return (DirectAgentChatRuntimeTests.CreateRuntime(factory, new Resolver(Lease(tool, dispose))),
            (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id));
    }

    private static DirectTurnCapabilityLease Lease(AIFunction tool, Action? dispose = null)
    {
        var scope = new DirectTurnLeaseScope();
        if (dispose is not null) scope.Add(new Resource(dispose));
        return new([], [new DirectToolBinding(tool, new(ToolCallKind.Other))], new Dictionary<Guid, string>(), [], scope);
    }

    private static AIFunction Tool(Func<object> call) => AIFunctionFactory.Create(call,
        new AIFunctionFactoryOptions { Name = "probe", MarshalResult = (result, _, _) => ValueTask.FromResult(result) });
    private static DirectToolResult Success() => new(ToolCallStatus.Completed, "ok", JsonSerializer.SerializeToElement("ok"), "ok");
    private static DirectToolResult SuccessAt(int execution)
        => new(ToolCallStatus.Completed, $"ok-{execution}", JsonSerializer.SerializeToElement($"ok-{execution}"),
            $"ok-{execution}");
    private static FunctionCallContent Call(string id) => new(id, "probe", new Dictionary<string, object?>());
    private static FunctionCallContent Unknown(string id) => new(id, $"missing-{id}", new Dictionary<string, object?>());
    private static ChatResponseUpdate Update(int n, ChatFinishReason? finish, params AIContent[] content)
        => new(ChatRole.Assistant, content) { MessageId = $"message-{n}", FinishReason = finish };
    private static async IAsyncEnumerable<ChatResponseUpdate> Reply(params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates) yield return update;
        await Task.CompletedTask;
    }
    private static async Task<List<AgentStreamEvent>> CollectAsync(DirectAgentChatRuntime runtime, DirectChatTurnRequest request,
        CancellationToken token = default)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var item in runtime.StreamTurnAsync(request, token)) events.Add(item);
        return events;
    }

    private sealed class Resolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request, ToolImageCapability imageCapability, CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }
    private sealed class Resource(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class FailingCheckpoint : IToolExecutionCheckpoint
    {
        public Task BeforeExecutionAsync(CancellationToken cancellationToken) => throw new IOException("checkpoint failed");
    }
    private sealed class CountingCheckpoint : IToolExecutionCheckpoint
    {
        internal int Calls { get; private set; }
        public Task BeforeExecutionAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
    }
    private sealed class Provider(Func<int, ChatMessage[], CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream) : IChatClient
    {
        internal int Requests { get; private set; }
        internal List<ChatMessage[]> Histories { get; } = [];
        internal bool Disposed { get; private set; }
        internal Action? OnDispose { get; init; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var history = messages.ToArray();
            Histories.Add(history);
            return stream(++Requests, history, cancellationToken);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { Disposed = true; OnDispose?.Invoke(); }
    }
}
