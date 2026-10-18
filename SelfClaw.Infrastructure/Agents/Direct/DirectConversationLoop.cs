using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct;

internal sealed class DirectConversationLoop
{
    internal const int MaximumProviderRequestsPerTurn = 128;

    /// <summary>
    /// How many in-turn compactions one turn may attempt. A failed attempt counts too: the cap bounds the
    /// latency and cost a summary can add to a single turn, not only the successful summaries.
    /// </summary>
    internal const int MaximumInTurnCompactionsPerTurn = 3;

    private readonly IDirectTokenCounter _tokenCounter;
    private readonly DirectPromptComposer _promptComposer;
    private readonly DirectContextCompressor _contextCompressor;
    private readonly IAttachmentContentReader _attachmentContentReader;

    internal DirectConversationLoop(
        IDirectTokenCounter tokenCounter,
        DirectPromptComposer promptComposer,
        DirectContextCompressor contextCompressor,
        IAttachmentContentReader attachmentContentReader)
    {
        _tokenCounter = tokenCounter;
        _promptComposer = promptComposer;
        _contextCompressor = contextCompressor;
        _attachmentContentReader = attachmentContentReader;
    }

    public async Task<DirectLoopOutcome> RunAsync(DirectTurnSetup setup, DirectEventTranslator output,
        CancellationToken cancellationToken)
    {
        var context = new DirectConversationContext(setup.SystemText, setup.InitialUnits, setup.TrailingProtectedMessages);
        // The loop is a singleton, so the per-turn allowance lives in this local rather than in a field.
        var compactionBudget = new InTurnCompactionBudget();
        for (var requestIndex = 0; requestIndex < MaximumProviderRequestsPerTurn; requestIndex++)
        {
            var isLastRequest = requestIndex + 1 == MaximumProviderRequestsPerTurn;
            cancellationToken.ThrowIfCancellationRequested();
            // The checkpoint precedes steer intake so a summary can free the room a pending batch needs. The
            // steer-only continuation path returns here through its continue and needs no second checkpoint.
            if (requestIndex > 0)
            {
                await TryCompactInTurnAsync(setup, context, output, compactionBudget, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (await ConsumeAvailableInputsAsync(setup, context, output, cancellationToken).ConfigureAwait(false) is { } budgetOutcome)
                return budgetOutcome;
            var response = await ReadResponseAsync(setup, context, output, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (GetTerminalOutcome(response) is { } terminal)
            {
                if (terminal == DirectLoopOutcome.ContentFiltered)
                    CancelCalls(setup, output, response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>(),
                        "ProviderContentFilter", "The provider filtered or refused this response; the tool was not executed.");
                return terminal;
            }

            var calls = context.AppendResponse(response, setup.CapabilityLease.Bindings);
            if (calls.Length == 0)
            {
                if (setup.InputSession is not { } inputs) return DirectLoopOutcome.Completed;
                // The plain read catches a batch without closing; the final read then either claims one
                // more accepted steer or closes the intake, so an accept during the check is not lost.
                var batch = await inputs.ReadBoundaryAsync(cancellationToken).ConfigureAwait(false);
                batch ??= await inputs.ReadFinalBoundaryAsync(cancellationToken).ConfigureAwait(false);
                if (batch is null) return DirectLoopOutcome.Completed;
                if (isLastRequest) return DirectLoopOutcome.BudgetExhausted;
                if (await TryConsumeBatchAsync(setup, inputs, batch, context, output, cancellationToken)
                        .ConfigureAwait(false) is { } held)
                    return held;
                continue;
            }
            if (isLastRequest)
            {
                CancelCalls(setup, output, calls, "BudgetExhausted",
                    $"The turn reached its {MaximumProviderRequestsPerTurn} provider request limit before this tool could execute.");
                return DirectLoopOutcome.BudgetExhausted;
            }

            context.AppendResults(await ExecuteToolsAsync(setup, output, calls, cancellationToken).ConfigureAwait(false));
        }
        throw new InvalidOperationException("The provider request budget was not resolved.");
    }

    private async Task<DirectLoopOutcome?> ConsumeAvailableInputsAsync(DirectTurnSetup setup,
        DirectConversationContext context, DirectEventTranslator output, CancellationToken cancellationToken)
    {
        if (setup.InputSession is not { } inputs) return null;
        while (await inputs.ReadBoundaryAsync(cancellationToken).ConfigureAwait(false) is { } batch)
        {
            if (await TryConsumeBatchAsync(setup, inputs, batch, context, output, cancellationToken)
                    .ConfigureAwait(false) is { } held)
                return held;
        }

        return null;
    }

    // Null means the claimed batch was consumed and the turn may continue; a non-null outcome ends the turn.
    private async Task<DirectLoopOutcome?> TryConsumeBatchAsync(DirectTurnSetup setup, IDirectTurnInputSession inputs,
        ConversationInputBatch batch, DirectConversationContext context, DirectEventTranslator output,
        CancellationToken cancellationToken)
    {
        if (!FitsBudget(setup, context, batch))
        {
            // The turn cannot carry the claimed batch without silently dropping protected content.
            await inputs.HoldUnconsumedAsync(ConversationInputReason.ContextBudgetExceeded, cancellationToken)
                .ConfigureAwait(false);
            return DirectLoopOutcome.ContextBudgetExceeded;
        }

        output.PublishInputBoundary(batch);
        var consumed = await inputs.WaitForCommitAsync(batch, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Consumed steer images are read once, before they join the context: the next provider request sends
        // these bytes, so counting and the request itself must see the same content.
        var attachmentContents = await _attachmentContentReader.ReadAsync(consumed.Messages, cancellationToken)
            .ConfigureAwait(false);
        context.AppendInputs(consumed.Messages, attachmentContents);
        output.BeginInputSegment();
        return null;
    }

    /// <summary>
    /// Refuses a steer batch only when the declared window would be exceeded; the transcript that was already
    /// consumed is never trimmed. Counting can be unavailable at runtime, and design §5.5 degrades fail-open
    /// there: the batch is consumed and the provider decides whether the request fits.
    /// </summary>
    private bool FitsBudget(DirectTurnSetup setup, DirectConversationContext context, ConversationInputBatch batch)
    {
        var profile = setup.ProviderLease.Profile;
        if (AiChatOptions.ResolveContextWindowTokens(profile) is not int contextWindow)
            return true;
        if (CountPrompt(setup, context) is not { } counted)
            return true;
        var total = (long)counted.Total;
        foreach (var input in batch.Inputs)
        {
            if (_tokenCounter.CountText(input.Prompt, profile) is not int promptTokens)
                return true;
            total += promptTokens;
        }

        return total <= contextWindow;
    }

    /// <summary>
    /// The in-turn compaction checkpoint (design §3.1). Every early return means "send this turn uncompacted":
    /// a failed summary, unavailable counting or an exhausted allowance never removes content or ends the turn.
    /// Only a compaction that still leaves the prompt over the window fails the turn (decision A).
    /// </summary>
    private async Task TryCompactInTurnAsync(DirectTurnSetup setup, DirectConversationContext context,
        DirectEventTranslator output, InTurnCompactionBudget budget, CancellationToken cancellationToken)
    {
        if (!setup.CompressionPolicy.Enabled || budget.IsExhausted)
            return;
        if (CountPrompt(setup, context) is not { } counted)
            return;
        if (counted.Total < DirectPromptComposer.ResolveThreshold(setup.CompressionPolicy, setup.Budget))
            return;
        if (CountUnits(context, setup.ProviderLease.Profile) is not { } units)
            return;
        var plan = _promptComposer.PlanCompression(units, existing: null, setup.CompressionPolicy, setup.Budget,
            counted.Used + counted.ToolTokens);
        if (!plan.ShouldCompress)
            return;

        // Started precedes the summarization call, exactly like the turn-start path, so the transcript never
        // sees a lone terminal event for this checkpoint.
        output.PublishInTurnCompactionStarted(counted.Used + counted.ToolTokens, plan.ThresholdTokens);
        output.PublishRunStatus(AgentRunStatus.Compacting);
        var result = await _contextCompressor.TryCompressAsync(new DirectCompressionRequest(
                plan.PrefixUnits,
                // Already the bare summary: the compressor wraps it as the previous summary itself.
                context.InTurnSummary,
                plan.SourceTokens ?? counted.Used + counted.ToolTokens,
                setup.CompressionPolicy.ModelProfileId,
                setup.ProviderLease,
                setup.ConversationId,
                setup.TurnId),
            cancellationToken).ConfigureAwait(false);
        budget.RecordAttempt();
        if (result is null)
        {
            // A failed summary is a diagnostic, not a turn failure: the uncompacted history still goes out.
            output.PublishInTurnCompactionFailed(DirectAgentChatRuntime.CompactionFailedReason);
            output.PublishRunStatus(AgentRunStatus.Requesting);
            return;
        }

        CompleteInTurnCompaction(setup, context, output, plan, result, counted.ToolTokens);
    }

    private void CompleteInTurnCompaction(DirectTurnSetup setup, DirectConversationContext context,
        DirectEventTranslator output, DirectCompressionPlan plan, DirectCompressionResult result, int toolTokens)
    {
        // The bare summary goes in: the context owns the system-section shell, so a second compaction reuses
        // one format instead of nesting wrapped summaries.
        context.ApplyInTurnCompaction(result.Summary, plan.PrefixUnits.Count);
        if (result.UsedFallbackModel)
        {
            output.PublishRunStatus(AgentRunStatus.Compacting, DirectAgentChatRuntime.CompactionFallbackDiagnostic);
        }

        output.PublishInTurnCompactionCompleted(new InTurnContextCompactionCompletedEvent(
            plan.PrefixUnits.Count,
            result.SourceTokens,
            result.SummaryTokens,
            result.Model));
        EnsureInTurnPromptFitsWindow(setup, context, toolTokens);
        output.PublishRunStatus(AgentRunStatus.Requesting);
    }

    /// <summary>
    /// A compaction that still leaves the prompt over the window cannot be sent without silently trimming, so
    /// the turn reports it instead (decision A). Without a count nothing is known here and the provider decides.
    /// </summary>
    private void EnsureInTurnPromptFitsWindow(DirectTurnSetup setup, DirectConversationContext context, int toolTokens)
    {
        if (setup.Budget.ContextWindowTokens is not int contextWindow)
            return;
        if (context.CountTokens(_tokenCounter, setup.ProviderLease.Profile) is not int recounted)
            return;

        var reserve = setup.ProviderLease.Options.MaxOutputTokens ?? DirectPromptComposer.DefaultOutputTokenReserve;
        var required = (long)recounted + toolTokens + reserve;
        if (required <= contextWindow)
            return;

        throw new InvalidDataException(
            "In-turn context compaction left the prompt over the model context window: the remaining history, " +
            $"tool definitions and output reserve require approximately {required} tokens, exceeding the window " +
            $"of {contextWindow} tokens. Lower the output limit or use a model with a larger context window.");
    }

    /// <summary>
    /// The prompt size both steer admission and the in-turn checkpoint decide against: context, tool
    /// definitions and the output reserve. Null means counting is unavailable, which degrades fail-open
    /// (design §5.5) instead of substituting an estimate.
    /// </summary>
    private (int Used, int ToolTokens, int Total)? CountPrompt(DirectTurnSetup setup, DirectConversationContext context)
    {
        var profile = setup.ProviderLease.Profile;
        if (context.CountTokens(_tokenCounter, profile) is not int used)
            return null;
        if (_tokenCounter.CountTools(setup.ProviderLease.Options.Tools, profile) is not int toolTokens)
            return null;
        var reserve = setup.ProviderLease.Options.MaxOutputTokens ?? DirectPromptComposer.DefaultOutputTokenReserve;
        return (used, toolTokens, used + toolTokens + reserve);
    }

    // A unit without a count cannot be sized, so planning degrades to "not this checkpoint" instead of guessing.
    private IReadOnlyList<DirectPromptHistoryUnit>? CountUnits(DirectConversationContext context, AiModelProfile profile)
    {
        var units = context.BuildUnits();
        var counted = new List<DirectPromptHistoryUnit>(units.Count);
        foreach (var unit in units)
        {
            if (_tokenCounter.CountMessages(unit.Messages, profile) is not int tokens)
                return null;
            counted.Add(unit with { Tokens = tokens });
        }

        return counted;
    }

    private static DirectLoopOutcome? GetTerminalOutcome(ChatResponse response)
    {
        var contents = response.Messages.SelectMany(message => message.Contents).ToArray();
        if (response.FinishReason == ChatFinishReason.ContentFilter || contents.OfType<ErrorContent>().Any(error => error.ErrorCode == "Refusal"))
            return DirectLoopOutcome.ContentFiltered;
        if (contents.OfType<ErrorContent>().FirstOrDefault() is { } error)
            throw new InvalidDataException($"The provider returned an error: {error.Message}");
        if (response.FinishReason == ChatFinishReason.Length) return DirectLoopOutcome.Length;
        if (response.FinishReason is { } reason && reason != ChatFinishReason.Stop && reason != ChatFinishReason.ToolCalls)
            throw new InvalidDataException($"The provider returned an unsupported finish reason: '{reason}'.");
        if (response.FinishReason is null && !contents.Any(HasResponseContent))
            throw new InvalidDataException("The provider returned no response content or completion information.");
        return null;
    }

    private static bool HasResponseContent(AIContent content) => content switch
    {
        TextContent text => !string.IsNullOrWhiteSpace(text.Text),
        TextReasoningContent reasoning => !string.IsNullOrWhiteSpace(reasoning.Text) || !string.IsNullOrEmpty(reasoning.ProtectedData),
        UsageContent or ErrorContent => false,
        DataContent data => !data.Data.IsEmpty,
        UriContent or ToolCallContent or ToolResultContent => true,
        _ => content.RawRepresentation is not null
    };

    private static void CancelCalls(DirectTurnSetup setup, DirectEventTranslator output,
        IEnumerable<FunctionCallContent> calls, string reason, string detail)
    {
        foreach (var call in calls.DistinctBy(call => call.CallId))
            PublishResult(setup, output, new FunctionResultContent(call.CallId, new DirectToolResult(
                ToolCallStatus.Canceled, reason, JsonSerializer.SerializeToElement(new { reason }), detail)));
    }

    private static async Task<IList<AIContent>> ExecuteToolsAsync(DirectTurnSetup setup,
        DirectEventTranslator output, FunctionCallContent[] calls, CancellationToken cancellationToken)
    {
        var results = new List<AIContent>();
        foreach (var call in calls)
        {
            FunctionResultContent result;
            try
            {
                result = new FunctionResultContent(call.CallId,
                    await setup.Invoker.InvokeAsync(call, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                PublishResult(setup, output, new FunctionResultContent(call.CallId, null) { Exception = exception });
                throw;
            }
            results.Add(result);
            PublishResult(setup, output, result);
        }
        return results;
    }

    private static async Task<ChatResponse> ReadResponseAsync(DirectTurnSetup setup,
        DirectConversationContext context, DirectEventTranslator output, CancellationToken cancellationToken)
    {
        var usage = new RequestUsageAccumulator(setup.ProviderLease.UsageUpdateKind);
        try
        {
            // ToChatResponseAsync aggregates incrementally, preserving native content and metadata.
            // It consumes through the END of the response, including calls emitted after finish_reason.
            var response = await TranslateResponseAsync(setup, context, output, usage, cancellationToken)
                .ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            // Preserve observed usage even if the provider fails after reporting it.
            if (usage.Build() is { } observed) output.ObserveUsage(observed);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TranslateResponseAsync(DirectTurnSetup setup,
        DirectConversationContext context, DirectEventTranslator output, RequestUsageAccumulator usage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in setup.ProviderLease.Client.GetStreamingResponseAsync(
            context.Messages, setup.ProviderLease.Options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents.OfType<UsageContent>()) usage.Observe(content.Details);
            output.TranslateUpdate(update, setup.CapabilityLease.Bindings);
            yield return update;
        }
    }

    private static void PublishResult(DirectTurnSetup setup, DirectEventTranslator output, FunctionResultContent result)
        => output.TranslateUpdate(new ChatResponseUpdate(ChatRole.Tool, [result]), setup.CapabilityLease.Bindings);

    /// <summary>
    /// The per-turn compaction allowance. DirectConversationLoop is a DI singleton, so attempts may never
    /// accumulate in an instance field; RunAsync creates one of these and threads it through its checkpoints.
    /// </summary>
    private sealed class InTurnCompactionBudget
    {
        private int _attempts;

        internal bool IsExhausted => _attempts >= MaximumInTurnCompactionsPerTurn;

        internal void RecordAttempt() => _attempts++;
    }
}
