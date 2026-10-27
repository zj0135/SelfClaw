using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Core.Runtime.Tooling;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// In-process Direct runtime. It translates Microsoft.Extensions.AI streaming
/// content into the same provider-neutral event stream consumed by the desktop transcript.
/// </summary>
internal sealed class DirectAgentChatRuntime : IAgentRuntimeAdapter
{
    /// <summary>
    /// Reported when the model stops at its output-token cap. Hitting the cap is normal
    /// once the limit is configured, so this is informational rather than an error.
    /// </summary>
    private const string TruncatedMessage =
        "The response reached the configured output-token limit. Continue the message to " +
        "have the model resume from where it stopped.";

    private static readonly string ToolLoopExhaustedMessage =
        $"BudgetExhausted: the turn reached its {DirectConversationLoop.MaximumProviderRequestsPerTurn} provider request limit before it could continue.";

    private static readonly string StalledMessage =
        $"NoProgress: the turn repeated the same tool calls with the same results {NoProgressGuard.RepeatedBatchLimit} times in a row and was stopped to avoid an endless loop. Tell the agent what to do differently and try again.";

    /// <summary>
    /// Reported when the output-token cap is hit before any text is produced. There is no
    /// partial answer to resume from, so the limit is likely configured too low to be usable.
    /// </summary>
    private const string TruncatedWithoutOutputMessage =
        "The response reached the configured output-token limit without producing any output. " +
        "Raise the output-token limit for this model and try again.";

    internal const string CompactionFailedReason =
        "Context compaction failed; the turn continues with the uncompacted history.";

    internal const string CompactionFallbackDiagnostic =
        "The configured compaction model was unavailable; the turn's own model produced the summary.";

    private const string CountingUnavailableDiagnostic =
        "Token counting is unavailable for this model, so context compaction and the window check are off for this turn.";

    private readonly IAiChatClientFactory _chatClientFactory;
    private readonly IDirectTurnCapabilityResolver _capabilityResolver;
    private readonly DirectPromptComposer _promptComposer;
    private readonly ILogger<DirectAgentChatRuntime> _logger;
    private readonly DirectConversationLoop _conversationLoop;
    private readonly IDirectTokenCounter _tokenCounter;
    private readonly DirectContextCompressor _contextCompressor;
    private readonly IDirectCompressionPolicySource _compressionPolicy;
    private readonly IAttachmentContentReader _attachmentContentReader;

    public DirectAgentChatRuntime(
        IAiChatClientFactory chatClientFactory,
        IDirectTurnCapabilityResolver capabilityResolver,
        DirectPromptComposer promptComposer,
        DirectConversationLoop conversationLoop,
        IDirectTokenCounter tokenCounter,
        DirectContextCompressor contextCompressor,
        IAttachmentContentReader attachmentContentReader,
        IDirectCompressionPolicySource? compressionPolicy = null,
        ILogger<DirectAgentChatRuntime>? logger = null)
    {
        _chatClientFactory = chatClientFactory;
        _capabilityResolver = capabilityResolver;
        _promptComposer = promptComposer;
        _conversationLoop = conversationLoop;
        _tokenCounter = tokenCounter;
        _contextCompressor = contextCompressor;
        _attachmentContentReader = attachmentContentReader;
        _compressionPolicy = compressionPolicy ?? DisabledDirectCompressionPolicySource.Instance;
        _logger = logger ?? NullLogger<DirectAgentChatRuntime>.Instance;
    }

    public AgentExecutionMode Mode => AgentExecutionMode.Direct;

    public IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The dispatcher routes by Mode, so a Direct turn always arrives as a DirectChatTurnRequest.
        var directRequest = request as DirectChatTurnRequest
            ?? throw new ArgumentException(
                $"The Direct runtime requires a {nameof(DirectChatTurnRequest)}.", nameof(request));
        return StreamCoreAsync(directRequest, cancellationToken);
    }

    private async IAsyncEnumerable<AgentStreamEvent> StreamCoreAsync(
        DirectChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        // The linked source lets an abandoned enumerator (consumer stops reading without
        // cancelling) still tear down the provider stream instead of orphaning it.
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceEventsAsync(request, channel.Writer, producerCancellation.Token);

        try
        {
            await foreach (var streamEvent in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            producerCancellation.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceEventsAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        var output = new DirectEventTranslator(writer);
        DirectTurnSetup? setup = null;
        try
        {
            setup = await SetupTurnAsync(request, writer, cancellationToken).ConfigureAwait(false);
            output.ConfigureUsage(
                setup.ProviderLease.Profile.Configuration,
                setup.ProviderLease.Profile.Model,
                AiChatOptions.ResolveContextWindowTokens(setup.ProviderLease.Profile));
            var outcome = await _conversationLoop.RunAsync(setup, output, cancellationToken).ConfigureAwait(false);
            output.ReportUsage();
            WriteTerminalOutcome(writer, outcome, output);
        }
        catch (OperationCanceledException exception)
        {
            output.ReportUsage();
            writer.TryComplete(exception);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Direct AI agent turn failed.");
            output.ReportUsage();
            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                exception.Message));
        }
        finally
        {
            if (setup is not null)
            {
                await DisposeResourcesAsync(setup.ProviderLease, setup.CapabilityLease).ConfigureAwait(false);
            }

            // Every path above writes exactly one terminal; the dispatcher synthesizes one if a future
            // path ever misses, so this producer does not keep a second copy of that guarantee.
            writer.TryComplete();
        }
    }

    private async Task<DirectTurnSetup> SetupTurnAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        if ((request.InputSession is not null || request.Steering is not null) &&
            request.ExecutionContext.Origin != DirectTurnOrigin.Interactive)
            throw new InvalidDataException("Only Interactive Direct turns can consume input boundaries or steer.");

        if (DirectTurnPolicy.For(request).RequiresToolExecutionCheckpoint && request.ToolExecutionCheckpoint is null)
        {
            throw new InvalidDataException("A continuation requires a durable tool execution checkpoint.");
        }

        // Rejected here, before any provider connection or capability lease exists, because the batch is what
        // makes the prompt describe itself as delivering Subagent results: a turn that cannot deliver them must
        // not reach the composer at all.
        if (request.ExecutionContext.CompletionBatch is not null &&
            request.ExecutionContext.Origin != DirectTurnOrigin.Continuation)
        {
            throw new InvalidDataException("Only a continuation turn can carry a Subagent completion batch.");
        }

        var preparation = await _chatClientFactory.PrepareAsync(request.ModelProfileId, cancellationToken).ConfigureAwait(false);
        request = request with { ModelProfileId = preparation.Profile.Id };
        var capabilityLease = await _capabilityResolver.ResolveAsync(request, cancellationToken)
            .ConfigureAwait(false);
        AiChatClientLease? providerLease = null;
        try
        {
            foreach (var diagnostic in capabilityLease.Diagnostics)
            {
                writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, diagnostic));
            }

            // Publish the steer policy before the request starts: an input accepted during initialization
            // is converted to a follow-up here rather than being steered into an already-active skill turn.
            if (request.Steering is not null)
            {
                var steerBlockReason = capabilityLease.ExplicitSkillIds.Count > 0
                    ? ConversationInputReason.SkillRequiresNewTurn
                    : null;
                await request.Steering.SetSteerPolicyAsync(steerBlockReason, cancellationToken).ConfigureAwait(false);
            }

            var invoker = new DirectToolInvoker(request, capabilityLease.Bindings);
            providerLease = _chatClientFactory.Create(
                preparation,
                new AiChatClientPipelineOptions(capabilityLease.Tools));

            // Direct has no resumable session: the whole conversation is replayed into every turn, so only
            // the CLI parsers ever report a session id.
            writer.TryWrite(new RunStartedEvent(
                SessionId: null,
                providerLease.Profile.Model,
                AgentKind: null));

            var budget = new DirectPromptBudget(
                AiChatOptions.ResolveContextWindowTokens(providerLease.Profile),
                providerLease.Options.MaxOutputTokens);
            // The units are built once and shared by counting, compaction planning and the prompt itself:
            // rebuilding them per request would rescan the whole stored history on every turn. Images are
            // read before the units so counting, planning and the request measure the same bytes.
            var attachmentContents = await _attachmentContentReader.ReadAsync(request.Messages, cancellationToken)
                .ConfigureAwait(false);
            var units = _promptComposer.BuildUnits(
                request.Messages,
                request.Turns,
                request.ToolExecutions ?? [],
                capabilityLease.MessageAdjustments,
                attachmentContents);
            // The plan is read from the stored history before compaction runs, so what it decides is independent
            // of the units this turn happens to replay.
            var plan = TodoPlanHistory.FindLatest(request.Messages, request.ToolExecutions ?? []);
            var policy = await _compressionPolicy.GetAsync(cancellationToken).ConfigureAwait(false);
            var (compression, countedTokens, todoSection) = await ApplyCompactionAsync(
                    request, capabilityLease, providerLease, budget, policy, units, plan, writer, cancellationToken)
                .ConfigureAwait(false);
            EnsurePromptFitsWindow(budget, countedTokens);
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));

            // The section is the one compaction already counted, so the request that is sized and the request
            // that is sent can never disagree about it.
            var build = DirectPromptComposer.Compose(
                units,
                _promptComposer.BuildSystemText(
                    request.Agent.Instructions,
                    capabilityLease.SystemInstructions,
                    request.ExecutionContext,
                    compression,
                    todoSection),
                DirectPromptComposer.BuildTrailingProtectedMessages(request.ExecutionContext),
                compression);
            return new DirectTurnSetup(
                capabilityLease,
                providerLease,
                invoker,
                build.SystemText,
                build.Units,
                build.TrailingProtectedMessages,
                policy,
                budget,
                request.ConversationId,
                request.TurnId,
                request.InputSession);
        }
        catch
        {
            // Ownership transfers to the producer only after the whole setup succeeds.
            await DisposeResourcesAsync(providerLease, capabilityLease).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Decides and performs this turn's compaction: count, plan, summarize, and publish the events the
    /// transcript needs. Runs before the first provider request, so the summary is part of that request.
    /// Returns the compaction the prompt should replay against, the token count of that prompt and the plan
    /// section it was counted with; a null count means counting is unavailable and the turn degrades to an
    /// uncompacted, unchecked request (§5.5).
    /// </summary>
    private async Task<(ConversationCompactionRecord? Compression, int? PromptTokens, string? TodoSection)>
        ApplyCompactionAsync(
            DirectChatTurnRequest request,
            DirectTurnCapabilityLease capabilityLease,
            AiChatClientLease providerLease,
            DirectPromptBudget budget,
            DirectCompressionPolicy policy,
            IReadOnlyList<DirectPromptHistoryUnit> units,
            TodoPlanSnapshot? planSnapshot,
            ChannelWriter<AgentStreamEvent> writer,
            CancellationToken cancellationToken)
    {
        var section = TodoSection(planSnapshot, request.Compression);
        // Without a declared window and with compaction off, there is nothing to count: the meter waits for
        // the provider's own measurement instead of paying for a local one nobody decides on.
        if (budget.ContextWindowTokens is null && !policy.Enabled)
        {
            return (request.Compression, null, section);
        }

        var (promptTokens, countedUnits) = CountPromptTokens(
            request, capabilityLease, providerLease, units, request.Compression, section);
        if (promptTokens is not int estimatedTokens)
        {
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, CountingUnavailableDiagnostic));
            return (request.Compression, null, section);
        }

        var plan = _promptComposer.PlanCompression(countedUnits, request.Compression, policy, budget, estimatedTokens);
        if (!plan.ShouldCompress || plan.ThroughSequence is not long throughSequence)
        {
            // No estimate here: the meter keeps the last measured context and updates when this request
            // reports its own, so the displayed number has exactly one source instead of two close ones.
            return (request.Compression, estimatedTokens, section);
        }

        // Started is written before the summarization call so the transcript never sees a lone terminal, and
        // EstimatedTokens stays reserve-free to match the context-size readout (design §8.1).
        writer.TryWrite(new ContextCompactionStartedEvent(estimatedTokens, plan.ThresholdTokens));
        writer.TryWrite(new RunStatusEvent(AgentRunStatus.Compacting));
        var result = await _contextCompressor.TryCompressAsync(new DirectCompressionRequest(
                plan.PrefixUnits,
                request.Compression?.Summary,
                plan.SourceTokens ?? estimatedTokens,
                policy.ModelProfileId,
                providerLease,
                request.ConversationId,
                request.TurnId),
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // A failed summary is a diagnostic, not a turn failure: the uncompacted history still goes out.
            writer.TryWrite(new ContextCompactionFailedEvent(CompactionFailedReason));
            return (request.Compression, estimatedTokens, section);
        }

        var compression = new ConversationCompactionRecord(
            Guid.NewGuid(),
            request.ConversationId,
            request.TurnId,
            throughSequence,
            result.Summary,
            result.Model,
            result.SourceMessageCount,
            result.SourceTokens,
            result.SummaryTokens,
            DateTimeOffset.UtcNow);
        if (result.UsedFallbackModel)
        {
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Compacting, CompactionFallbackDiagnostic));
        }

        writer.TryWrite(new ContextCompactionCompletedEvent(compression));
        // The window check bounds what is actually sent, so it is re-run against the prompt that now carries
        // the summary section in its system text. The estimate published from it is the post-summary size, so
        // the meter shows the drop here rather than the pre-compaction figure.
        var compactedSection = TodoSection(planSnapshot, compression);
        var (recountedTokens, _) = CountPromptTokens(
            request, capabilityLease, providerLease, units, compression, compactedSection);
        if (recountedTokens is int recounted)
        {
            PublishCountedContext(writer, recounted, providerLease);
        }

        return (compression, recountedTokens, compactedSection);
    }

    // The only place the snapshot rule lives: a plan is worth a section exactly when the boundary this request
    // replays against already swallowed the message that carried it.
    internal static string? TodoSection(TodoPlanSnapshot? plan, ConversationCompactionRecord? compression)
        => plan is { } snapshot && compression is { } boundary && snapshot.SourceSequence <= boundary.ThroughSequence
            ? TodoPlanSection.Build(snapshot.Plan)
            : null;

    /// <summary>
    /// Publishes the prompt size a completed compaction left behind. This is the one place an estimate is
    /// shown: the context really did shrink and no provider has measured the compacted prompt yet, so waiting
    /// for the next observation would keep the pre-compaction size on screen.
    /// </summary>
    private static void PublishCountedContext(ChannelWriter<AgentStreamEvent> writer, int promptTokens,
        AiChatClientLease providerLease)
        => writer.TryWrite(DirectEventTranslator.CreateContextEstimate(
            promptTokens, AiChatOptions.ResolveContextWindowTokens(providerLease.Profile)));

    /// <summary>
    /// Counts the prompt the composer is about to build: the system sections (including an existing summary),
    /// the tool definitions and every unit that still gets replayed. Units an existing summary already covers
    /// stay uncounted because they never reach the request. Null means counting is unavailable (§5.5).
    /// </summary>
    private (int? PromptTokens, IReadOnlyList<DirectPromptHistoryUnit> Units) CountPromptTokens(
        DirectChatTurnRequest request,
        DirectTurnCapabilityLease capabilityLease,
        AiChatClientLease providerLease,
        IReadOnlyList<DirectPromptHistoryUnit> units,
        ConversationCompactionRecord? compression,
        string? todoSection)
    {
        var profile = providerLease.Profile;
        var systemText = _promptComposer.BuildSystemText(
            request.Agent.Instructions,
            capabilityLease.SystemInstructions,
            request.ExecutionContext,
            compression,
            todoSection);
        if (_tokenCounter.CountText(systemText, profile) is not int systemTokens ||
            _tokenCounter.CountTools(providerLease.Options.Tools, profile) is not int toolTokens)
        {
            return (null, units);
        }

        var total = systemTokens + toolTokens;
        var counted = new List<DirectPromptHistoryUnit>(units.Count);
        foreach (var unit in units)
        {
            if (compression is not null && unit.SourceSequence <= compression.ThroughSequence)
            {
                counted.Add(unit);
                continue;
            }

            if (_tokenCounter.CountMessages(unit.Messages, profile) is not int unitTokens)
            {
                return (null, units);
            }

            counted.Add(unit with { Tokens = unitTokens });
            total += unitTokens;
        }

        return (total, counted);
    }

    /// <summary>
    /// Refuses a prompt that cannot fit even after compaction. Neither history nor instructions are trimmed
    /// silently, so an oversized turn is reported instead of being sent. Without a count nothing is known
    /// here and the provider decides (design §5.5).
    /// </summary>
    private static void EnsurePromptFitsWindow(DirectPromptBudget budget, int? countedTokens)
    {
        if (budget.ContextWindowTokens is not int contextWindow || countedTokens is not int counted)
        {
            return;
        }

        var required = counted + (budget.MaxOutputTokens ?? DirectPromptComposer.DefaultOutputTokenReserve);
        if (required <= contextWindow)
        {
            return;
        }

        throw new InvalidDataException(
            "The system instructions, tool definitions, and replayable history require approximately " +
            $"{required} tokens once the output reserve is added, exceeding the model context window of " +
            $"{contextWindow} tokens. Enable context compaction, lower the output limit, or use a model with " +
            "a larger context window.");
    }

    /// <summary>
    /// Emits the terminal RunCompleted event for the observed finish reason. A length stop with
    /// partial text is reported as Truncated (the partial answer is valid and kept in the prompt
    /// history, so the model can resume from it if the user continues); a length stop without text
    /// and an exhausted tool loop are both reported as Failed.
    /// </summary>
    private void WriteTerminalOutcome(
        ChannelWriter<AgentStreamEvent> writer,
        DirectLoopOutcome outcome,
        DirectEventTranslator output)
    {
        if (outcome == DirectLoopOutcome.ContentFiltered)
        {
            Write(writer, RunCompletionStatus.Blocked, output.FinalTextOrNull,
                "The provider filtered or refused the response (ContentFilter).");
            return;
        }

        if (outcome == DirectLoopOutcome.Length && output.HasFinalText)
        {
            _logger.LogInformation(
                "Direct AI agent turn stopped at the output-token cap; reporting it as truncated.");
            Write(writer, RunCompletionStatus.Truncated, output.FinalText, TruncatedMessage);
            return;
        }

        if (outcome == DirectLoopOutcome.Length)
        {
            _logger.LogWarning(
                "Direct AI agent turn hit the output-token cap without producing any text.");
            Write(writer, RunCompletionStatus.Failed, null, TruncatedWithoutOutputMessage);
            return;
        }

        if (outcome == DirectLoopOutcome.ContextBudgetExceeded)
        {
            _logger.LogWarning("Direct AI agent turn could not fit a steer batch into the model context window.");
            Write(writer, RunCompletionStatus.Failed, output.FinalTextOrNull,
                "ContextBudgetExceeded: the queued steer could not fit in the model context window; the unconsumed input was held.");
            return;
        }

        if (outcome == DirectLoopOutcome.BudgetExhausted)
        {
            _logger.LogWarning("Direct AI agent turn exhausted its {RequestLimit} provider request budget.",
                DirectConversationLoop.MaximumProviderRequestsPerTurn);
            Write(writer, RunCompletionStatus.Failed, output.FinalTextOrNull, ToolLoopExhaustedMessage);
            return;
        }

        if (outcome == DirectLoopOutcome.Stalled)
        {
            _logger.LogWarning(
                "Direct AI agent turn repeated the same tool batch {RepeatLimit} times and was stopped as stalled.",
                NoProgressGuard.RepeatedBatchLimit);
            Write(writer, RunCompletionStatus.Failed, output.FinalTextOrNull, StalledMessage);
            return;
        }

        Write(writer, RunCompletionStatus.Succeeded, output.FinalText, null);
    }

    private static void Write(ChannelWriter<AgentStreamEvent> writer, RunCompletionStatus status,
        string? finalText, string? errorMessage = null)
        => writer.TryWrite(new RunCompletedEvent(status, finalText, errorMessage));

    private async Task DisposeResourcesAsync(
        AiChatClientLease? providerLease,
        DirectTurnCapabilityLease capabilityLease)
    {
        try
        {
            providerLease?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose the Direct AI chat client pipeline.");
        }

        try
        {
            await capabilityLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose the Direct turn capability lease.");
        }
    }
}
