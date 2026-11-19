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
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Tests.TestDoubles;
using Xunit;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

/// <summary>
/// C7 wiring: the runtime decides, performs and announces context compaction before the first provider
/// request of a turn, and never lets a failed summary fail an otherwise valid turn.
/// </summary>
public sealed class DirectCompactionRuntimeTests
{
    private const string Summary = "summarized history";

    /// <summary>
    /// The threshold the measurement tests decide against. The deterministic counter sizes the prompt at 123
    /// (system text 28, units 32 + 32 + 31) and the reserve adds 50, so the local trigger value of 173 stays
    /// below it while the units alone (95) still exceed the retention target of 90.
    /// </summary>
    private const int MeasuredThreshold = 180;

    /// <summary>No declared window and a small absolute threshold: the trigger is decided without a budget.</summary>
    private static DirectCompressionPolicy TriggeringPolicy()
        => new(Enabled: true, AbsoluteThresholdTokens: 10);

    /// <summary>A window far above the prompt: the trigger is decided by the absolute threshold instead.</summary>
    private static DirectCompressionPolicy IdlePolicy()
        => new(Enabled: true, ThresholdRatio: 0.75d, AbsoluteThresholdTokens: 4_000_000);

    [Fact]
    public async Task Below_the_threshold_the_turn_sends_the_full_history_without_compaction_events()
    {
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]]);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client, contextWindowTokens: 100_000);
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, compressionPolicy: new FixedPolicySource(IdlePolicy()));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory)));

        events.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        events.OfType<ContextCompactionCompletedEvent>().Should().BeEmpty();
        events.OfType<ContextCompactionFailedEvent>().Should().BeEmpty();
        client.SummaryCalls.Should().BeEmpty("no summarization request is made when the trigger is not reached");
        client.Invocations.Should().ContainSingle();
        client.Invocations[0].Should().Contain(message => message.Text == "prior prompt");
        client.Invocations[0].Should().Contain(message => message.Text == "prior answer");
        client.Invocations[0].Should().Contain(message => message.Text == "user prompt");
    }

    [Fact]
    public async Task Reaching_the_threshold_publishes_started_then_completed_and_replays_the_summary()
    {
        var client = new TurnClient(
            [
                [Finish("m1", ChatFinishReason.ToolCalls, new FunctionCallContent("call-1", "probe", new Dictionary<string, object?>()))],
                [Finish("m2", ChatFinishReason.Stop, new TextContent("done"))]
            ])
        {
            SummaryText = Summary
        };
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new FixedResolver(Lease(Tool())), new FixedPolicySource(TriggeringPolicy()));
        var request = CreateRequest(factory);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        // The turn-start pair only: a turn this far over its threshold also compacts in-turn between provider
        // round trips, and those events belong to DirectInTurnCompactionTests.
        var compactionEvents = events
            .Where(item => item is ContextCompactionStartedEvent or ContextCompactionCompletedEvent)
            .Take(2)
            .ToList();
        compactionEvents.Should().HaveCount(2);
        compactionEvents[0].Should().BeOfType<ContextCompactionStartedEvent>();
        compactionEvents[1].Should().BeOfType<ContextCompactionCompletedEvent>();
        events.IndexOf(compactionEvents[0]).Should().BeLessThan(events.IndexOf(compactionEvents[1]));
        events.OfType<ContextCompactionFailedEvent>().Should().BeEmpty();

        // The started event reports the prompt size without the output reserve (design §8.1).
        var started = (ContextCompactionStartedEvent)compactionEvents[0];
        started.EstimatedTokens.Should().BePositive();
        started.ThresholdTokens.Should().Be(10);

        var completed = (ContextCompactionCompletedEvent)compactionEvents[1];
        completed.Compaction.Id.Should().NotBe(Guid.Empty);
        completed.Compaction.ConversationId.Should().Be(request.ConversationId);
        completed.Compaction.TurnId.Should().Be(request.TurnId);
        completed.Compaction.Summary.Should().Be(Summary);
        completed.Compaction.Model.Should().Be(factory.Profile.Model);
        completed.Compaction.SourceMessageCount.Should().Be(2);
        completed.Compaction.SourceTokens.Should().BePositive();
        completed.Compaction.CreatedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        // Compacting must be overridden by the request it precedes, which is already the compacted one.
        var compactingIndex = IndexOfStatus(events, AgentRunStatus.Compacting);
        compactingIndex.Should().BeGreaterThan(0);
        compactingIndex.Should().BeLessThan(IndexOfStatus(events, AgentRunStatus.Requesting));
        client.Invocations.Should().HaveCount(2);
        client.Invocations[0].Should().NotContain(message => message.Text == "prior prompt");
        client.Invocations[0].Should().Contain(message => message.Text == "user prompt",
            "turn-start compaction replays the retained tail in the request it shapes");

        // Every request of the turn replays the boundary and the summary section instead of the prefix. The
        // second request may have been compacted again in-turn (this threshold is far below the prompt), which
        // only tightens what it carries.
        var replayed = client.Invocations[1];
        replayed[0].Role.Should().Be(ChatRole.System);
        replayed[0].Text.Should().Contain("selfclaw-context-summary");
        replayed[0].Text.Should().Contain(Summary);
        replayed.Should().NotContain(message => message.Text == "prior prompt");
        replayed.Count(message => message.Role == ChatRole.Tool).Should().Be(1);
    }

    [Fact]
    public async Task A_failed_summary_is_reported_as_a_diagnostic_and_the_turn_continues_uncompacted()
    {
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]]);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, compressionPolicy: new FixedPolicySource(TriggeringPolicy()));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory)));

        events.OfType<ContextCompactionStartedEvent>().Should().ContainSingle();
        var failed = events.OfType<ContextCompactionFailedEvent>().Should().ContainSingle().Which;
        failed.Reason.Should().NotBeNullOrWhiteSpace();
        events.OfType<ContextCompactionCompletedEvent>().Should().BeEmpty();
        client.Invocations.Should().ContainSingle();
        client.Invocations[0].Should().Contain(message => message.Text == "prior prompt");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status
            .Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Disabled_compaction_over_the_window_fails_readably()
    {
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]]);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client, contextWindowTokens: 60)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory)));

        events.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        client.Invocations.Should().BeEmpty();
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Which;
        terminal.Status.Should().Be(RunCompletionStatus.Failed);
        terminal.ErrorMessage.Should().Contain("model context window");
        terminal.ErrorMessage.Should().Contain("context compaction");
        terminal.ErrorMessage.Should().Contain("lower the output limit");
        terminal.ErrorMessage.Should().Contain("larger context window");
    }

    [Fact]
    public async Task Unavailable_counting_skips_compaction_without_failing_the_turn()
    {
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]]);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client, contextWindowTokens: 100_000);
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(
            factory,
            compressionPolicy: new FixedPolicySource(TriggeringPolicy()),
            tokenCounter: UnavailableTokenCounter.Instance);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory)));

        events.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        events.OfType<ContextCompactionCompletedEvent>().Should().BeEmpty();
        events.OfType<ContextCompactionFailedEvent>().Should().BeEmpty();
        client.SummaryCalls.Should().BeEmpty();
        events.OfType<RunStatusEvent>()
            .Where(status => status.Detail != null && status.Detail.Contains("Token counting is unavailable"))
            .Should().ContainSingle();
        client.Invocations.Should().ContainSingle();
        client.Invocations[0].Should().Contain(message => message.Text == "prior prompt");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status
            .Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Cancelling_the_summary_propagates_without_a_failure_event()
    {
        using var stop = new CancellationTokenSource();
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]])
        {
            ResponseException = new OperationCanceledException(),
            BeforeResponseException = stop.Cancel
        };
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, compressionPolicy: new FixedPolicySource(TriggeringPolicy()));
        var seen = new List<AgentStreamEvent>();

        var action = async () =>
        {
            await foreach (var item in runtime.StreamTurnAsync(CreateRequest(factory), stop.Token))
            {
                seen.Add(item);
            }
        };

        await action.Should().ThrowAsync<OperationCanceledException>();
        seen.OfType<ContextCompactionStartedEvent>().Should().ContainSingle();
        seen.OfType<ContextCompactionFailedEvent>().Should().BeEmpty();
        seen.OfType<RunCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_on_the_oldest_message_does_not_stop_turn_start_compaction()
    {
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]])
        {
            SummaryText = Summary
        };
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var request = CreateRequest(factory);
        var reader = new ScriptedAttachmentContentReader()
            .WithImage(MessageOf(request, "prior prompt").Id, "prior.png")
            .WithImage(MessageOf(request, "user prompt").Id, "current.png");
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory,
            compressionPolicy: new FixedPolicySource(TriggeringPolicy()), attachmentContentReader: reader);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        // The oldest candidate carries an image and the boundary still crosses it: only the newest unit is
        // retained at this threshold, so the image-bearing message and the answer after it are the prefix.
        var completed = events.OfType<ContextCompactionCompletedEvent>().Should().ContainSingle().Which;
        completed.Compaction.ThroughSequence.Should().Be(MessageOf(request, "prior answer").Sequence);
        completed.Compaction.SourceMessageCount.Should().Be(2);

        var summarized = client.SummaryCalls.Should().ContainSingle().Subject;
        Images(summarized).Should().BeEmpty("the summarizer never receives binary content");
        summarized.SelectMany(message => message.Contents).OfType<TextContent>()
            .Should().Contain(text => text.Text == "[image: prior.png]");

        var sent = client.Invocations.Should().ContainSingle().Subject;
        sent.Should().NotContain(message => message.Text == "prior answer");
        Images(sent).Should().ContainSingle("only the retained message still carries its image")
            .Which.Name.Should().Be("current.png");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status
            .Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task An_image_the_stored_summary_already_covers_is_not_read_again()
    {
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]]);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client);
        var request = CreateRequest(factory);
        var boundary = MessageOf(request, "prior answer");
        request = request with
        {
            Compression = new ConversationCompactionRecord(Guid.NewGuid(), request.ConversationId, boundary.TurnId,
                boundary.Sequence, Summary, "test-model", 2, 10, 5, DateTimeOffset.UtcNow)
        };
        var userPrompt = MessageOf(request, "user prompt");
        var reader = new ScriptedAttachmentContentReader()
            .WithImage(MessageOf(request, "prior prompt").Id, "prior.png")
            .WithImage(userPrompt.Id, "current.png");
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, attachmentContentReader: reader);

        await CollectAsync(runtime.StreamTurnAsync(request));

        reader.RequestedMessageIds.Should().Equal([userPrompt.Id],
            "a message behind the boundary never reaches a provider again, so its bytes are not read either");
        Images(client.Invocations.Should().ContainSingle().Subject).Should().ContainSingle()
            .Which.Name.Should().Be("current.png");
    }

    [Fact]
    public async Task The_preceding_turns_measurement_starts_a_compaction_the_local_count_would_not()
    {
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]])
        {
            SummaryText = Summary
        };
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        // The preceding turn's last request measured exactly the threshold; that is the size the transcript shows.
        var request = WithPrecedingMeasurement(CreateRequest(factory), MeasuredThreshold);

        var events = await CollectAsync(MeasuredRuntime(factory).StreamTurnAsync(request));

        var started = events.OfType<ContextCompactionStartedEvent>().Should().ContainSingle().Which;
        started.EstimatedTokens.Should().Be(123, "the event keeps reporting the local, reserve-free count");
        started.ThresholdTokens.Should().Be(MeasuredThreshold);
        // The measurement only decides whether to compact. The prefix is still sized by the unit counts: the
        // retention target of 90 keeps the two newest units (31 + 32).
        events.OfType<ContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.Compaction.ThroughSequence.Should().Be(MessageOf(request, "prior prompt").Sequence);
        var sent = client.Invocations.Should().ContainSingle().Subject;
        sent.Should().NotContain(message => message.Text == "prior prompt");
        sent.Should().Contain(message => message.Text == "prior answer");
    }

    [Fact]
    public Task A_preceding_turn_that_reported_nothing_leaves_the_local_count_to_decide()
        => AssertTheLocalCountDecidesAsync(request => request);

    [Fact]
    public Task A_measurement_older_than_the_preceding_turn_is_never_used()
        => AssertTheLocalCountDecidesAsync(request =>
        {
            // A later turn that reported nothing sits between the measurement and this turn. An older
            // measurement may predate the stored boundary, so it must never be picked up instead.
            var current = request.Turns.Single(turn => turn.Id == request.TurnId);
            var silent = new ConversationTurnRecord(Guid.NewGuid(), request.ConversationId, AgentExecutionMode.Direct,
                DirectTurnOrigin.Interactive, ConversationTurnStatus.Failed, current.StartedAtUtc.AddSeconds(-30),
                current.StartedAtUtc);
            var measured = WithPrecedingMeasurement(request, 10 * MeasuredThreshold);
            return measured with { Turns = [.. measured.Turns, silent] };
        });

    [Fact]
    public Task A_preceding_cli_turn_never_supplies_the_measurement()
        => AssertTheLocalCountDecidesAsync(request =>
        {
            // A CLI turn owns its context: whatever it reports describes the CLI session, not this prompt.
            var measured = WithPrecedingMeasurement(request, 10 * MeasuredThreshold);
            return measured with
            {
                Turns = measured.Turns
                    .Select(turn => turn.Id == request.TurnId ? turn : turn with { ExecutionMode = AgentExecutionMode.Cli })
                    .ToArray()
            };
        });

    // The prompt counts 173 with its reserve against a threshold of 180, so nothing but a qualifying
    // measurement can start a compaction here.
    private static async Task AssertTheLocalCountDecidesAsync(Func<DirectChatTurnRequest, DirectChatTurnRequest> shape)
    {
        var client = new TurnClient([[Finish("m1", ChatFinishReason.Stop, new TextContent("done"))]])
        {
            SummaryText = Summary
        };
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };

        var events = await CollectAsync(MeasuredRuntime(factory).StreamTurnAsync(shape(CreateRequest(factory))));

        events.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        client.SummaryCalls.Should().BeEmpty();
        client.Invocations.Should().ContainSingle().Which.Should().Contain(message => message.Text == "prior prompt");
    }

    private static DirectAgentChatRuntime MeasuredRuntime(DirectAgentChatRuntimeTests.FakeChatClientFactory factory)
        => DirectAgentChatRuntimeTests.CreateRuntime(
            factory,
            new FixedResolver(Lease()),
            new FixedPolicySource(new DirectCompressionPolicy(Enabled: true, AbsoluteThresholdTokens: MeasuredThreshold)),
            new DirectInTurnCompactionTests.CountingTokenCounter());

    // Every turn but the running one reports this as the input of its last provider request.
    private static DirectChatTurnRequest WithPrecedingMeasurement(DirectChatTurnRequest request, int inputTokens)
        => request with
        {
            Turns = request.Turns
                .Select(turn => turn.Id == request.TurnId
                    ? turn
                    : turn with { Usage = new TurnUsage(LastRequestInputTokens: inputTokens) })
                .ToArray()
        };

    private static MessageRecord MessageOf(DirectChatTurnRequest request, string text)
        => request.Messages.Single(message => message.MarkdownContent == text);

    private static DataContent[] Images(IReadOnlyList<ChatMessage> messages)
        => messages.SelectMany(message => message.Contents).OfType<DataContent>().ToArray();

    private static DirectChatTurnRequest CreateRequest(DirectAgentChatRuntimeTests.FakeChatClientFactory factory)
        => (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id);

    private static int IndexOfStatus(IReadOnlyList<AgentStreamEvent> events, AgentRunStatus status)
    {
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index] is RunStatusEvent runStatus && runStatus.Status == status)
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> events)
    {
        var result = new List<AgentStreamEvent>();
        await foreach (var item in events)
        {
            result.Add(item);
        }

        return result;
    }

    private static DirectTurnCapabilityLease Lease(params AIFunction[] tools)
        => new(
            [],
            tools.Select(tool => new DirectToolBinding(tool, new DirectToolDescriptor(ToolCallKind.Other))).ToArray(),
            new Dictionary<Guid, string>(),
            []);

    private static AIFunction Tool() => AIFunctionFactory.Create(
        () => new DirectToolResult(ToolCallStatus.Completed, "ok", JsonSerializer.SerializeToElement("ok"), "ok"),
        new AIFunctionFactoryOptions
        {
            Name = "probe",
            MarshalResult = (value, _, _) => ValueTask.FromResult(value)
        });

    private static ChatResponseUpdate Finish(string messageId, ChatFinishReason finishReason, params AIContent[] contents)
        => new(ChatRole.Assistant, contents) { MessageId = messageId, FinishReason = finishReason };

    private sealed class FixedPolicySource(DirectCompressionPolicy policy) : IDirectCompressionPolicySource
    {
        public Task<DirectCompressionPolicy> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(policy);
    }

    private sealed class FixedResolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(
            DirectChatTurnRequest request,
            ToolImageCapability imageCapability,
            CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    private sealed class UnavailableTokenCounter : IDirectTokenCounter
    {
        internal static UnavailableTokenCounter Instance { get; } = new();

        public int? CountText(string? text, AiModelProfile? model) => null;

        public int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model) => null;

        public int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model) => null;
    }

    /// <summary>
    /// Serves the turn with scripted streaming updates and the summarization call with one fixed summary,
    /// because both calls go through the same chat client lease.
    /// </summary>
    private sealed class TurnClient : IChatClient
    {
        private readonly IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> _scripts;

        internal TurnClient(IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> scripts) => _scripts = scripts;

        /// <summary>Null makes the summarization call fail the way an unsupported client would.</summary>
        internal string? SummaryText { get; init; }

        internal Exception? ResponseException { get; init; }

        /// <summary>Lets a test cancel the turn's own token at the moment the summary call fails.</summary>
        internal Action? BeforeResponseException { get; init; }

        internal List<IReadOnlyList<ChatMessage>> Invocations { get; } = [];
        internal List<IReadOnlyList<ChatMessage>> SummaryCalls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the compressor summarizes over the streaming path");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var captured = messages.ToList();
            if (captured.Count > 0 && captured[0].Role == ChatRole.System && captured[0].Text == CompressionInstruction.Text)
            {
                SummaryCalls.Add(captured);
                if (ResponseException is not null)
                {
                    BeforeResponseException?.Invoke();
                    throw ResponseException;
                }

                if (SummaryText is null)
                {
                    throw new NotSupportedException();
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(SummaryText)]);
                yield break;
            }

            var index = Math.Min(Invocations.Count, _scripts.Count - 1);
            Invocations.Add(captured);
            foreach (var update in _scripts[index])
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
