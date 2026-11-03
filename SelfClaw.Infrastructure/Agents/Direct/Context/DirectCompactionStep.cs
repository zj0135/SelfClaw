using System.Threading.Channels;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// The turn's single compaction orchestration: count, plan, summarize, publish the events the transcript needs
/// and republish the context estimate. The turn-start path (the boundary is the stored record) and the in-turn
/// checkpoint (the boundary is the summary this turn already holds) share every step except where the boundary
/// comes from and how the result is applied. A failed summary is always a diagnostic, never a turn failure.
/// </summary>
internal sealed class DirectCompactionStep
{
    internal const string CompactionFailedReason =
        "Context compaction failed; the turn continues with the uncompacted history.";

    internal const string CompactionFallbackDiagnostic =
        "The configured compaction model was unavailable; the turn's own model produced the summary.";

    internal const string CountingUnavailableDiagnostic =
        "Token counting is unavailable for this model, so context compaction and the window check are off for this turn.";

    /// <summary>
    /// How many in-turn compactions one turn may attempt. A failed attempt counts too: the cap bounds the
    /// latency and cost a summary can add to a single turn, not only the successful summaries. It is sized so a
    /// long task that keeps growing the context is not forced to give up after a few rounds.
    /// </summary>
    internal const int MaximumInTurnCompactionsPerTurn = 10;

    private readonly DirectPromptMeter _meter;
    private readonly DirectContextCompressor _compressor;

    internal DirectCompactionStep(
        DirectPromptMeter meter,
        DirectContextCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(compressor);
        _meter = meter;
        _compressor = compressor;
    }

    /// <summary>
    /// Decides and performs the turn-start compaction, before the first provider request so its summary is part
    /// of that request. Returns the boundary the prompt should replay against, the measurement of the prompt
    /// that carries it and the plan section it was counted with; a null measurement means counting is
    /// unavailable and the turn degrades to an uncompacted, unchecked request (design §5.5).
    /// </summary>
    internal async Task<DirectTurnStartCompaction> CompactTurnStartAsync(
        DirectTurnCompaction input,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var section = TodoSection(input.PlanSnapshot, input.Request.Compression);
        // Without a declared window and with compaction off, there is nothing to count: the meter waits for the
        // provider's own measurement instead of paying for a local one nobody decides on.
        if (input.Budget.ContextWindowTokens is null && !input.Policy.Enabled)
        {
            return new DirectTurnStartCompaction(input.Request.Compression, null, section);
        }

        if (CountTurnStart(input, input.Request.Compression, section) is not { } counted)
        {
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, CountingUnavailableDiagnostic));
            return new DirectTurnStartCompaction(input.Request.Compression, null, section);
        }

        var plan = DirectCompactionPlanner.PlanCompression(
            counted.Units, input.Request.Compression, input.Policy, input.Budget, counted.PromptTokens);
        if (!plan.ShouldCompress)
        {
            // No estimate here: the meter keeps the last measured context and updates when this request reports
            // its own, so the displayed number has exactly one source instead of two close ones.
            return new DirectTurnStartCompaction(input.Request.Compression, counted, section);
        }

        // Started is written before the summarization call so the transcript never sees a lone terminal, and
        // EstimatedTokens stays reserve-free to match the context-size readout (design §8.1).
        writer.TryWrite(new ContextCompactionStartedEvent(counted.PromptTokens, plan.ThresholdTokens));
        writer.TryWrite(new RunStatusEvent(AgentRunStatus.Compacting));
        var result = await SummarizeAsync(
            input.Request.ConversationId, input.Request.TurnId, input.Policy, input.ProviderLease, plan,
            input.Request.Compression?.Summary, counted.PromptTokens, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            writer.TryWrite(new ContextCompactionFailedEvent(CompactionFailedReason));
            return new DirectTurnStartCompaction(input.Request.Compression, counted, section);
        }

        var compression = CreateRecord(input.Request, plan, result);
        if (result.UsedFallbackModel)
        {
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Compacting, CompactionFallbackDiagnostic));
        }

        writer.TryWrite(new ContextCompactionCompletedEvent(compression));
        // The window check bounds what is actually sent, so it is re-run against the prompt that now carries the
        // summary section in its system text. The estimate published from it is the post-summary size, so the
        // meter shows the drop here rather than the pre-compaction figure.
        var compactedSection = TodoSection(input.PlanSnapshot, compression);
        var recounted = CountTurnStart(input, compression, compactedSection);
        if (recounted is not null)
        {
            writer.TryWrite(DirectEventTranslator.CreateContextEstimate(
                recounted.PromptTokens, AiChatOptions.ResolveContextWindowTokens(input.ProviderLease.Profile)));
        }

        return new DirectTurnStartCompaction(compression, recounted, compactedSection);
    }

    /// <summary>
    /// The in-turn compaction checkpoint (design §3.1). Every early return means "send this turn uncompacted":
    /// a failed summary, unavailable counting or an exhausted allowance never removes content or ends the turn.
    /// Only a compaction that still leaves the prompt over the window fails the turn (decision A).
    /// </summary>
    internal async Task CompactInTurnAsync(
        DirectTurnSetup setup,
        DirectConversationContext context,
        InTurnCompactionAllowance allowance,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allowance);
        if (!setup.CompressionPolicy.Enabled || allowance.IsExhausted)
        {
            return;
        }

        if (MeasureContext(setup, context) is not { } measured)
        {
            return;
        }

        // The reserve belongs to the trigger side: a prompt just under the threshold can still overflow once
        // the response is written, and compacting is the only way to make room.
        if (measured.TotalWithReserve < DirectCompactionPlanner.ResolveThreshold(setup.CompressionPolicy, setup.Budget))
        {
            return;
        }

        if (_meter.CountUnits(context.BuildUnits(), setup.ProviderLease.Profile) is not { } units)
        {
            return;
        }

        var plan = DirectCompactionPlanner.PlanCompression(units, existing: null, setup.CompressionPolicy, setup.Budget,
            measured.PromptTokens);
        if (!plan.ShouldCompress)
        {
            return;
        }

        // Started precedes the summarization call, exactly like the turn-start path, so the transcript never
        // sees a lone terminal event for this checkpoint.
        writer.TryWrite(new ContextCompactionStartedEvent(measured.PromptTokens, plan.ThresholdTokens));
        writer.TryWrite(new RunStatusEvent(AgentRunStatus.Compacting));
        var result = await SummarizeAsync(
            setup.ConversationId, setup.TurnId, setup.CompressionPolicy, setup.ProviderLease, plan,
            // Already the bare summary: the compressor wraps it as the previous summary itself.
            context.InTurnSummary, measured.PromptTokens, cancellationToken).ConfigureAwait(false);
        allowance.RecordAttempt();
        if (result is null)
        {
            writer.TryWrite(new ContextCompactionFailedEvent(CompactionFailedReason));
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));
            return;
        }

        ApplyInTurnCompaction(setup, context, writer, plan, result);
    }

    /// <summary>
    /// Refuses a prompt that cannot fit even after compaction. Neither history nor instructions are trimmed
    /// silently, so an oversized turn is reported instead of being sent. Without a count nothing is known here
    /// and the provider decides (design §5.5).
    /// </summary>
    internal static void EnsureTurnStartPromptFitsWindow(DirectPromptBudget budget, DirectTurnStartCompaction compaction)
    {
        if (budget.ContextWindowTokens is not int contextWindow || compaction.Counted is not { } counted)
        {
            return;
        }

        if (counted.TotalWithReserve <= contextWindow)
        {
            return;
        }

        throw new InvalidDataException(
            "The system instructions, tool definitions, and replayable history require approximately " +
            $"{counted.TotalWithReserve} tokens once the output reserve is added, exceeding the model context window of " +
            $"{contextWindow} tokens. Enable context compaction, lower the output limit, or use a model with " +
            "a larger context window.");
    }

    /// <summary>
    /// Refuses an in-turn prompt that cannot fit, because sending it would come back as an opaque provider
    /// error. Runs before every request of the turn, not only after a summary: once the allowance is spent
    /// nothing shrinks the prompt any more, and the turn must say so itself.
    /// </summary>
    internal void EnsureInTurnPromptFitsWindow(
        DirectTurnSetup setup,
        DirectConversationContext context,
        bool compactionExhausted)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(context);
        // Ordered before counting: with no declared window there is nothing to compare against, so a model
        // that never declared one must not pay for a count here.
        if (setup.Budget.ContextWindowTokens is not int contextWindow)
        {
            return;
        }

        if (MeasureContext(setup, context) is not { } measured)
        {
            return;
        }

        if (measured.TotalWithReserve <= contextWindow)
        {
            return;
        }

        var remedy = compactionExhausted
            ? $"The turn used all {MaximumInTurnCompactionsPerTurn} of its in-turn compactions and still does not fit."
            : "Context compaction could not free enough room.";
        throw new InvalidDataException(
            "The conversation has outgrown the model context window: the remaining history, tool definitions " +
            $"and output reserve require approximately {measured.TotalWithReserve} tokens, exceeding the window of " +
            $"{contextWindow} tokens. {remedy} Lower the output limit, raise the compaction allowance, or use a " +
            "model with a larger context window.");
    }

    // The only place the snapshot rule lives: a plan is worth a section exactly when the boundary this request
    // replays against already swallowed the message that carried it.
    internal static string? TodoSection(TodoPlanSnapshot? plan, ConversationCompactionRecord? compression)
        => plan is { } snapshot && compression is { } boundary && snapshot.SourceSequence <= boundary.ThroughSequence
            ? TodoPlanSection.Build(snapshot.Plan)
            : null;

    /// <summary>
    /// Counts the prompt the turn-start path will build against <paramref name="boundary"/>. The section is the
    /// one compaction already counted, so the request that is sized and the request that is sent can never
    /// disagree about it.
    /// </summary>
    private DirectCountedPrompt? CountTurnStart(
        DirectTurnCompaction input,
        ConversationCompactionRecord? boundary,
        string? todoSection)
        => _meter.CountTurnStartPrompt(
            DirectSystemPrompt.BuildSystemText(
                input.Request.Agent.Instructions,
                input.CapabilityLease.SystemInstructions,
                input.Request.ExecutionContext,
                boundary,
                todoSection),
            input.ProviderLease.Options.Tools,
            input.ProviderLease.Profile,
            input.Units,
            boundary?.ThroughSequence ?? 0,
            DirectCompactionPlanner.ResolveOutputReserve(input.Budget.MaxOutputTokens));

    private DirectPromptMeasurement? MeasureContext(DirectTurnSetup setup, DirectConversationContext context)
        => _meter.CountContextPrompt(
            context,
            setup.ProviderLease.Options.Tools,
            setup.ProviderLease.Profile,
            DirectCompactionPlanner.ResolveOutputReserve(setup.ProviderLease.Options.MaxOutputTokens));

    private Task<DirectCompressionResult?> SummarizeAsync(
        Guid conversationId,
        Guid turnId,
        DirectCompressionPolicy policy,
        AiChatClientLease lease,
        DirectCompressionPlan plan,
        string? previousSummary,
        int promptTokens,
        CancellationToken cancellationToken)
        => _compressor.TryCompressAsync(
            new DirectCompressionRequest(
                plan.PrefixUnits,
                previousSummary,
                plan.SourceTokens ?? promptTokens,
                policy.ModelProfileId,
                lease,
                conversationId,
                turnId),
            cancellationToken);

    private static ConversationCompactionRecord CreateRecord(
        DirectChatTurnRequest request,
        DirectCompressionPlan plan,
        DirectCompressionResult result)
        => new(
            Guid.NewGuid(),
            request.ConversationId,
            request.TurnId,
            plan.ThroughSequence,
            result.Summary,
            result.Model,
            result.SourceMessageCount,
            result.SourceTokens,
            result.SummaryTokens,
            DateTimeOffset.UtcNow);

    private void ApplyInTurnCompaction(
        DirectTurnSetup setup,
        DirectConversationContext context,
        ChannelWriter<AgentStreamEvent> writer,
        DirectCompressionPlan plan,
        DirectCompressionResult result)
    {
        // The bare summary goes in: the context owns the system-section shell, so a second compaction reuses
        // one format instead of nesting wrapped summaries.
        context.ApplyInTurnCompaction(result.Summary, plan.PrefixUnits.Count);
        if (result.UsedFallbackModel)
        {
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Compacting, CompactionFallbackDiagnostic));
        }

        writer.TryWrite(new InTurnContextCompactionCompletedEvent(
            plan.PrefixUnits.Count,
            result.SourceTokens,
            result.SummaryTokens,
            result.Model));
        // The meter follows the compaction immediately: the published count is the compacted prompt, so the
        // context figure drops here instead of staying at the pre-compaction size until the next observation.
        if (MeasureContext(setup, context) is { } measured)
        {
            writer.TryWrite(DirectEventTranslator.CreateContextEstimate(
                measured.PromptTokens, setup.Budget.ContextWindowTokens));
        }

        writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));
    }
}
