using System.Threading.Channels;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Models;

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
    /// of that request. Returns the boundary the prompt replays against together with the plan section that was
    /// counted with it, so the request that is sized and the request that is sent cannot disagree. A null
    /// measurement means counting is unavailable (or nothing needed counting) and the turn degrades to an
    /// uncompacted, unchecked request (design §5.5).
    /// </summary>
    internal async Task<DirectTurnStartCompaction> CompactTurnStartAsync(
        DirectTurnCompaction input,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var uncompacted = DescribePrompt(input, input.Request.Compression);
        // Without a declared window and with compaction off, there is nothing to count: the meter waits for the
        // provider's own measurement instead of paying for a local one nobody decides on.
        if (input.Budget.ContextWindowTokens is null && !input.Policy.Enabled)
        {
            return uncompacted;
        }

        if (CountTurnStart(input, uncompacted) is not { } counted)
        {
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, CountingUnavailableDiagnostic));
            return uncompacted;
        }

        // No estimate is published for an uncompacted prompt: the meter keeps the last measured context and
        // updates when this request reports its own, so the displayed number has one source instead of two.
        uncompacted = uncompacted with { Counted = counted };
        var plan = DirectCompactionPlanner.PlanCompression(
            counted.Units, input.Request.Compression, input.Policy, input.Budget, counted.PromptTokens,
            ObservedContextTokens(input.Request));
        if (!plan.ShouldCompress)
        {
            return uncompacted;
        }

        var result = await SummarizeAsync(
            new DirectCompressionRequest(
                plan.PrefixUnits,
                input.Request.Compression?.Summary,
                plan.SourceTokens ?? counted.PromptTokens,
                input.Policy.ModelProfileId,
                input.ProviderLease,
                input.Request.ConversationId,
                input.Request.TurnId),
            counted.PromptTokens, plan.ThresholdTokens, writer, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return uncompacted;
        }

        var compression = new ConversationCompactionRecord(
            Guid.NewGuid(),
            input.Request.ConversationId,
            input.Request.TurnId,
            plan.ThroughSequence,
            result.Summary,
            result.Model,
            result.SourceMessageCount,
            result.SourceTokens,
            result.SummaryTokens,
            DateTimeOffset.UtcNow);
        writer.TryWrite(new ContextCompactionCompletedEvent(compression));
        // The window check bounds what is actually sent, so the prompt is counted again with the summary in
        // its system text. The estimate published from it is the post-summary size, so the meter shows the
        // drop here rather than the pre-compaction figure.
        var compacted = DescribePrompt(input, compression);
        var recounted = CountTurnStart(input, compacted);
        if (recounted is not null)
        {
            writer.TryWrite(DirectEventTranslator.CreateContextEstimate(
                recounted.PromptTokens, input.Budget.ContextWindowTokens));
        }

        return compacted with { Counted = recounted };
    }

    /// <summary>
    /// The in-turn checkpoint (design §3.1), run before every request after the first: compact when the
    /// context reached the trigger, then refuse a prompt that still cannot fit. A failed summary, unavailable
    /// counting or an exhausted allowance never removes content; only a prompt over the declared window fails
    /// the turn, because sending it would come back as an opaque provider error. The window check runs every
    /// round, not only after a summary: once the allowance is spent nothing shrinks the prompt any more.
    /// </summary>
    /// <param name="observedInputTokens">
    /// The provider's measurement of this turn's previous request. That prompt is a strict prefix of the one
    /// being checked (a response and its tool results were appended since, and a summary can only have been
    /// applied before that request was sent), so it is a safe lower bound for the trigger when the local count
    /// runs low: reasoning signatures, high-resolution images and a foreign tokenizer are all invisible to it.
    /// </param>
    internal async Task CheckpointInTurnAsync(
        DirectTurnSetup setup,
        DirectConversationContext context,
        InTurnCompactionAllowance allowance,
        int? observedInputTokens,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allowance);
        // Neither decision exists without a window or an enabled policy, so such a model pays for no count.
        if (setup.Budget.ContextWindowTokens is null && !setup.CompressionPolicy.Enabled)
        {
            return;
        }

        if (MeasureContext(setup, context) is not { } measured)
        {
            return;
        }

        if (PlanInTurn(setup, context, allowance, measured, observedInputTokens) is { } plan)
        {
            var result = await SummarizeAsync(
                new DirectCompressionRequest(
                    plan.PrefixUnits,
                    // Already the bare summary: the compressor wraps it as the previous summary itself. It is
                    // the request's only summary, so the new one stands for everything before the units left.
                    context.Summary,
                    plan.SourceTokens ?? measured.PromptTokens,
                    setup.CompressionPolicy.ModelProfileId,
                    setup.ProviderLease,
                    setup.ConversationId,
                    setup.TurnId),
                measured.PromptTokens, plan.ThresholdTokens, writer, cancellationToken).ConfigureAwait(false);
            allowance.RecordAttempt();
            if (result is not null)
            {
                measured = ApplyInTurnCompaction(setup, context, plan, result, writer) ?? measured;
            }

            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));
        }

        EnsureInTurnPromptFitsWindow(setup.Budget, measured, allowance.IsExhausted);
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

    // The only place the snapshot rule lives: a plan is worth a section exactly when the boundary this request
    // replays against already swallowed the call that carried it.
    internal static string? TodoSection(
        TodoPlanSnapshot? plan,
        IReadOnlyList<MessageRecord> messages,
        ConversationCompactionRecord? compression)
        => plan is { } snapshot && DirectHistoryReplay.IsCovered(messages, snapshot.Run, compression)
            ? TodoPlanSection.Build(snapshot.Plan)
            : null;

    // The prompt the turn would send against this boundary, not yet counted.
    private static DirectTurnStartCompaction DescribePrompt(
        DirectTurnCompaction input,
        ConversationCompactionRecord? boundary)
        => new(boundary, Counted: null, TodoSection(input.PlanSnapshot, input.Request.Messages, boundary));

    private DirectCountedPrompt? CountTurnStart(DirectTurnCompaction input, DirectTurnStartCompaction prompt)
        => _meter.CountTurnStartPrompt(
            DirectSystemPrompt.ComposeSystemText(input.FixedSystemText, prompt.Compression?.Summary, prompt.TodoSection),
            input.ProviderLease.Options.Tools,
            input.ProviderLease.Profile,
            input.Units,
            input.TrailingProtectedMessages,
            prompt.Compression?.ThroughSequence ?? 0,
            DirectCompactionPlanner.ResolveOutputReserve(input.Budget.MaxOutputTokens));

    private DirectPromptMeasurement? MeasureContext(DirectTurnSetup setup, DirectConversationContext context)
        => _meter.CountContextPrompt(
            context,
            setup.ProviderLease.Options.Tools,
            setup.ProviderLease.Profile,
            DirectCompactionPlanner.ResolveOutputReserve(setup.Budget.MaxOutputTokens));

    // The provider's own measurement of the context this turn starts from: the input of the last request the
    // preceding turn made, which is also the context size the transcript displays. Only the immediately
    // preceding turn qualifies. An older measurement can predate the stored boundary and would compact an
    // already compacted conversation again, so a preceding turn that reported nothing means "no floor",
    // never "look further back". A CLI turn measures the CLI's own session, not the prompt Direct replays.
    private static int? ObservedContextTokens(DirectChatTurnRequest request)
    {
        var preceding = request.Turns
            .Where(turn => turn.Id != request.TurnId)
            .MaxBy(turn => turn.StartedAtUtc);
        return preceding is { ExecutionMode: AgentExecutionMode.Direct }
            ? preceding.Usage?.LastRequestInputTokens
            : null;
    }

    // Null means "send this request uncompacted".
    private DirectCompressionPlan? PlanInTurn(
        DirectTurnSetup setup,
        DirectConversationContext context,
        InTurnCompactionAllowance allowance,
        DirectPromptMeasurement measured,
        int? observedInputTokens)
    {
        if (!setup.CompressionPolicy.Enabled || allowance.IsExhausted)
        {
            return null;
        }

        // The planner makes the same comparison; it is repeated here only because sizing every unit is the
        // expensive part and almost every round is below the trigger.
        var trigger = DirectCompactionPlanner.ResolveTrigger(setup.CompressionPolicy, setup.Budget);
        if (Math.Max(measured.PromptTokens, observedInputTokens ?? 0) < trigger)
        {
            return null;
        }

        if (_meter.CountUnits(context.BuildUnits(), setup.ProviderLease.Profile) is not { } units)
        {
            return null;
        }

        var plan = DirectCompactionPlanner.PlanCompression(
            units, existing: null, setup.CompressionPolicy, setup.Budget, measured.PromptTokens, observedInputTokens);
        return plan.ShouldCompress ? plan : null;
    }

    /// <summary>
    /// The summarization both paths share, with the events around it. Started is written before the call so the
    /// transcript never sees a lone terminal, and its token count stays reserve-free to match the context-size
    /// readout (design §8.1). Null means the summary failed and the Failed event is already written; the caller
    /// publishes its own Completed event because the two paths complete with different records.
    /// </summary>
    private async Task<DirectCompressionResult?> SummarizeAsync(
        DirectCompressionRequest request,
        int promptTokens,
        int thresholdTokens,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        writer.TryWrite(new ContextCompactionStartedEvent(promptTokens, thresholdTokens));
        writer.TryWrite(new RunStatusEvent(AgentRunStatus.Compacting));
        var result = await _compressor.TryCompressAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            writer.TryWrite(new ContextCompactionFailedEvent(CompactionFailedReason));
            return null;
        }

        if (result.UsedFallbackModel)
        {
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Compacting, CompactionFallbackDiagnostic));
        }

        return result;
    }

    // Returns the measurement of the compacted context, which is also what the meter is told: the context
    // figure drops here instead of staying at the pre-compaction size until the next provider observation.
    private DirectPromptMeasurement? ApplyInTurnCompaction(
        DirectTurnSetup setup,
        DirectConversationContext context,
        DirectCompressionPlan plan,
        DirectCompressionResult result,
        ChannelWriter<AgentStreamEvent> writer)
    {
        // The bare summary goes in: the context owns the system-section shell, so a second compaction reuses
        // one format instead of nesting wrapped summaries.
        var coverage = context.ApplyInTurnCompaction(result.Summary, plan.PrefixUnits.Count, result.SourceTokens);
        writer.TryWrite(new InTurnContextCompactionCompletedEvent(
            plan.PrefixUnits.Count,
            result.SourceTokens,
            result.SummaryTokens,
            result.Model,
            coverage is null ? null : CreateInTurnRecord(setup, context, coverage, result),
            coverage?.AnchorCallId));
        var compacted = MeasureContext(setup, context);
        if (compacted is not null)
        {
            writer.TryWrite(DirectEventTranslator.CreateContextEstimate(
                compacted.PromptTokens, setup.Budget.ContextWindowTokens));
        }

        return compacted;
    }

    // The record keeps the turn's one in-turn identity, so each later summary of the turn replaces it. It
    // carries no anchor yet: the recorder resolves the call id to the tool run it stored.
    private static ConversationCompactionRecord CreateInTurnRecord(
        DirectTurnSetup setup,
        DirectConversationContext context,
        DirectInTurnCoverage coverage,
        DirectCompressionResult result)
        => new(
            context.InTurnCompactionId,
            setup.ConversationId,
            setup.TurnId,
            coverage.ThroughSequence,
            result.Summary,
            result.Model,
            coverage.MessageCount,
            coverage.SourceTokens,
            result.SummaryTokens,
            DateTimeOffset.UtcNow);

    private static void EnsureInTurnPromptFitsWindow(
        DirectPromptBudget budget,
        DirectPromptMeasurement measured,
        bool compactionExhausted)
    {
        if (budget.ContextWindowTokens is not int contextWindow || measured.TotalWithReserve <= contextWindow)
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
}
