using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// One Direct turn's explicit loop. Everything that belongs to a single turn - the prepared setup, the live
/// context, the output, the compaction allowance and the progress guard - lives on this instance, so no per-turn
/// state can survive into the next turn and no helper has to thread it through. The stateless dependencies come
/// from <see cref="DirectTurnRunFactory"/>.
/// </summary>
internal sealed class DirectTurnRun
{
    /// <summary>
    /// A circuit breaker, not a task-length policy: a long task legitimately makes many requests, so the cap
    /// sits far above anything a healthy turn reaches. What actually stops a runaway turn is
    /// <see cref="NoProgressGuard"/> (a loop that repeats itself) plus the per-iteration window check;
    /// this number only bounds the pathological case where every request looks different and nothing fits.
    /// </summary>
    internal const int MaximumProviderRequestsPerTurn = 1024;

    private readonly DirectTurnSetup _setup;
    private readonly DirectEventTranslator _output;
    private readonly DirectConversationContext _context;
    private readonly InTurnCompactionAllowance _allowance = new();
    private readonly NoProgressGuard _progressGuard = new();
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private readonly DirectPromptMeter _meter;
    private readonly DirectCompactionStep _compactionStep;
    private readonly IAttachmentContentReader _attachmentContentReader;
    private int? _lastRequestInputTokens;

    internal DirectTurnRun(
        DirectTurnSetup setup,
        DirectEventTranslator output,
        DirectPromptMeter meter,
        DirectCompactionStep compactionStep,
        IAttachmentContentReader attachmentContentReader)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(compactionStep);
        ArgumentNullException.ThrowIfNull(attachmentContentReader);
        _setup = setup;
        _output = output;
        _meter = meter;
        _compactionStep = compactionStep;
        _attachmentContentReader = attachmentContentReader;
        _context = new DirectConversationContext(
            setup.FixedSystemText,
            setup.InitialUnits,
            setup.TrailingProtectedMessages,
            setup.PlanSection,
            setup.Summary,
            setup.HistoryThroughSequence);
    }

    internal async Task<DirectLoopOutcome> RunAsync(CancellationToken cancellationToken)
    {
        for (var requestIndex = 0; requestIndex < MaximumProviderRequestsPerTurn; requestIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestIndex > 0)
            {
                // The checkpoint precedes steer intake so a summary can free the room a pending batch needs. The
                // steer-only continuation path returns here through its null outcome and needs no second one.
                await _compactionStep.CheckpointInTurnAsync(
                        _setup, _context, _allowance, _lastRequestInputTokens, _output.Writer, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (await ConsumeAvailableInputsAsync(cancellationToken).ConfigureAwait(false) is { } budgetOutcome)
            {
                return budgetOutcome;
            }

            if (await RunIterationAsync(requestIndex + 1 == MaximumProviderRequestsPerTurn, cancellationToken)
                    .ConfigureAwait(false) is { } outcome)
            {
                return outcome;
            }
        }

        throw new InvalidOperationException("The provider request budget was not resolved.");
    }

    // Null means "send the next request"; a non-null outcome ends the turn.
    private async Task<DirectLoopOutcome?> RunIterationAsync(bool isLastRequest, CancellationToken cancellationToken)
    {
        var response = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        if (ResolveTerminal(response, calls) is { } terminal)
        {
            return terminal;
        }

        AppendResponse(response, calls);
        if (calls.Length == 0)
        {
            return await RunSteerContinuationAsync(isLastRequest, cancellationToken).ConfigureAwait(false);
        }

        if (isLastRequest)
        {
            CancelCalls(calls, DirectLoopMessages.BudgetExhaustedReason,
                DirectLoopMessages.BudgetExhaustedCanceledCall);
            return DirectLoopOutcome.BudgetExhausted;
        }

        var (results, followUps) = await ExecuteToolsAsync(calls, cancellationToken).ConfigureAwait(false);
        // A batch that repeats is a loop, and a loop cannot be told apart from a long task by counting
        // requests: the tools ran, nothing failed, and nothing new was produced. Their real results were
        // already published one event per call, so the terminal event alone explains the stop.
        if (_progressGuard.Observe(calls, results))
        {
            return DirectLoopOutcome.Stalled;
        }

        _context.AppendResults(results, followUps);
        return null;
    }

    /// <summary>
    /// Decides whether this response ends the turn. Every way of ending it here leaves the response's calls
    /// unexecuted, and their started events are already published, so each one gets its Canceled terminal
    /// before the outcome is returned or the provider error is thrown.
    /// </summary>
    private DirectLoopOutcome? ResolveTerminal(ChatResponse response, FunctionCallContent[] calls)
    {
        DirectLoopOutcome? terminal;
        try
        {
            terminal = GetTerminalOutcome(response);
        }
        catch (InvalidDataException)
        {
            CancelCalls(calls, DirectLoopMessages.ProviderErrorReason, DirectLoopMessages.ProviderErrorCanceledCall);
            throw;
        }

        switch (terminal)
        {
            case DirectLoopOutcome.ContentFiltered:
                CancelCalls(calls, DirectLoopMessages.ContentFilterReason,
                    DirectLoopMessages.ContentFilterCanceledCall);
                break;
            case DirectLoopOutcome.Length:
                CancelCalls(calls, DirectLoopMessages.OutputTruncatedReason,
                    DirectLoopMessages.OutputTruncatedCanceledCall);
                break;
        }

        return terminal;
    }

    /// <summary>
    /// Appends one provider response as a unit. Only the call-id identity and the tool_calls shape are checked
    /// here: an unknown tool name or unparseable arguments are model-side mistakes the invoker turns into
    /// Failed results, while a repeated id cannot be replayed at all.
    /// </summary>
    private void AppendResponse(ChatResponse response, FunctionCallContent[] calls)
    {
        foreach (var call in calls)
        {
            if (string.IsNullOrWhiteSpace(call.CallId) || !_callIds.Add(call.CallId))
            {
                throw new InvalidDataException($"Invalid or repeated Direct tool call id '{call.CallId}'.");
            }
        }

        if (calls.Length == 0 && response.FinishReason == ChatFinishReason.ToolCalls)
        {
            throw new InvalidDataException(
                "The provider ended with tool_calls but supplied no complete client tool call.");
        }

        _context.AppendResponse(response);
    }

    // The plain read catches a batch without closing; the final read then either claims one more accepted steer
    // or closes the intake, so an accept during the check is not lost.
    private async Task<DirectLoopOutcome?> RunSteerContinuationAsync(
        bool isLastRequest,
        CancellationToken cancellationToken)
    {
        if (_setup.InputSession is not { } inputs)
        {
            return DirectLoopOutcome.Completed;
        }

        var batch = await inputs.ReadBoundaryAsync(cancellationToken).ConfigureAwait(false);
        batch ??= await inputs.ReadFinalBoundaryAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return DirectLoopOutcome.Completed;
        }

        if (isLastRequest)
        {
            // No further request may consume this batch, so the loop settles the claim it just made instead of
            // leaving it claimed until the terminal commit.
            await inputs.HoldUnconsumedAsync(ConversationInputReason.TurnFailed, cancellationToken)
                .ConfigureAwait(false);
            return DirectLoopOutcome.BudgetExhausted;
        }

        if (!FitsBudget(batch))
        {
            // The model already gave its final answer; a late steer that no longer fits must not turn that
            // answer into a failed turn. The input is held with the reason and the turn ends as it stood.
            await inputs.HoldUnconsumedAsync(ConversationInputReason.ContextBudgetExceeded, cancellationToken)
                .ConfigureAwait(false);
            return DirectLoopOutcome.Completed;
        }

        await ConsumeBatchAsync(inputs, batch, cancellationToken).ConfigureAwait(false);
        return null;
    }

    // Null means every pending batch was consumed; a non-null outcome ends the turn.
    private async Task<DirectLoopOutcome?> ConsumeAvailableInputsAsync(CancellationToken cancellationToken)
    {
        if (_setup.InputSession is not { } inputs)
        {
            return null;
        }

        while (await inputs.ReadBoundaryAsync(cancellationToken).ConfigureAwait(false) is { } batch)
        {
            if (!FitsBudget(batch))
            {
                // Mid-task the turn cannot go on without the claimed batch, and cannot carry it without
                // silently dropping protected content.
                await inputs.HoldUnconsumedAsync(ConversationInputReason.ContextBudgetExceeded, cancellationToken)
                    .ConfigureAwait(false);
                return DirectLoopOutcome.ContextBudgetExceeded;
            }

            await ConsumeBatchAsync(inputs, batch, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task ConsumeBatchAsync(
        IDirectTurnInputSession inputs,
        ConversationInputBatch batch,
        CancellationToken cancellationToken)
    {
        _output.PublishInputBoundary(batch);
        var consumed = await inputs.WaitForCommitAsync(batch, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Consumed steer images are read once, before they join the context: the next provider request sends
        // these bytes, so counting and the request itself must see the same content.
        var attachmentContents = await _attachmentContentReader.ReadAsync(consumed.Messages, cancellationToken)
            .ConfigureAwait(false);
        _context.AppendInputs(consumed.Messages, attachmentContents);
        _output.BeginInputSegment();
        _progressGuard.Reset();
    }

    /// <summary>
    /// Refuses a steer batch only when the declared window would be exceeded; the transcript that was already
    /// consumed is never trimmed. Counting can be unavailable at runtime, and design §5.5 degrades fail-open
    /// there: the batch is consumed and the provider decides whether the request fits.
    /// </summary>
    private bool FitsBudget(ConversationInputBatch batch)
    {
        if (_setup.Budget.ContextWindowTokens is not int contextWindow)
        {
            return true;
        }

        var profile = _setup.ProviderLease.Profile;
        if (_meter.CountContextPrompt(_context, _setup.ProviderLease.Options.Tools, profile,
                DirectCompactionPlanner.ResolveOutputReserve(_setup.Budget.MaxOutputTokens))
            is not { } counted)
        {
            return true;
        }

        var total = (long)counted.TotalWithReserve;
        foreach (var input in batch.Inputs)
        {
            if (_meter.CountInput(input, profile) is not int promptTokens)
            {
                return true;
            }

            total += promptTokens;
        }

        return total <= contextWindow;
    }

    private static DirectLoopOutcome? GetTerminalOutcome(ChatResponse response)
    {
        var contents = response.Messages.SelectMany(message => message.Contents).ToArray();
        var errors = contents.OfType<ErrorContent>().ToArray();
        if (response.FinishReason == ChatFinishReason.ContentFilter || errors.Any(IsRefusal))
        {
            return DirectLoopOutcome.ContentFiltered;
        }

        // An empty refusal is a wire artifact, not a provider error: an OpenAI-compatible gateway that
        // stamps "refusal": "" on every frame would otherwise fail every turn it serves.
        if (errors.FirstOrDefault(error => !IsEmptyRefusal(error)) is { } error)
        {
            throw new InvalidDataException($"The provider returned an error: {error.Message}");
        }

        if (response.FinishReason == ChatFinishReason.Length)
        {
            return DirectLoopOutcome.Length;
        }

        if (response.FinishReason is { } reason &&
            reason != ChatFinishReason.Stop && reason != ChatFinishReason.ToolCalls)
        {
            throw new InvalidDataException($"The provider returned an unsupported finish reason: '{reason}'.");
        }

        if (response.FinishReason is null)
        {
            throw new InvalidDataException("The provider stream ended without a completion reason; the response may be incomplete.");
        }

        return null;
    }

    private static bool IsRefusal(ErrorContent error) =>
        error.ErrorCode == "Refusal" && !string.IsNullOrWhiteSpace(error.Message);

    private static bool IsEmptyRefusal(ErrorContent error) =>
        error.ErrorCode == "Refusal" && string.IsNullOrWhiteSpace(error.Message);

    // Every stop that leaves calls unexecuted publishes the same shape: one Canceled result per call, with the
    // reason as its summary and the human sentence as its detail.
    private void CancelCalls(IEnumerable<FunctionCallContent> calls, string reason, string detail)
    {
        foreach (var call in calls.DistinctBy(call => call.CallId))
        {
            _output.PublishToolResult(call.CallId, new DirectToolResult(
                ToolCallStatus.Canceled, reason, JsonSerializer.SerializeToElement(new { reason }), detail));
        }
    }

    private async Task<(IList<AIContent> Results, IReadOnlyList<ChatMessage> FollowUps)> ExecuteToolsAsync(
        FunctionCallContent[] calls,
        CancellationToken cancellationToken)
    {
        var results = new List<AIContent>();
        // Images the protocol can only deliver as a user message are collected for the whole batch: one user
        // message after every tool message, so no user turn can ever sit between two tool results.
        var followUpImages = new List<DataContent>();
        var questionReturned = false;
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            var direct = questionReturned
                ? new DirectToolResult(ToolCallStatus.Canceled, DirectLoopMessages.UserQuestionBoundaryReason,
                    JsonSerializer.SerializeToElement(new { reason = DirectLoopMessages.UserQuestionBoundaryReason,
                        message = DirectLoopMessages.UserQuestionBoundaryCanceledCall }),
                    DirectLoopMessages.UserQuestionBoundaryCanceledCall)
                : await ExecuteToolAsync(calls, index, cancellationToken).ConfigureAwait(false);

            // Exactly one completion event per call, published from the invoker's own result type.
            results.Add(new FunctionResultContent(
                call.CallId, ToolResultImagePayload.Create(direct, _setup.ImageDelivery, followUpImages)));
            _output.PublishToolResult(call.CallId, direct);
            // Even an invalid questionnaire needs a new model decision before any preplanned work can run.
            if (!string.IsNullOrWhiteSpace(call.Name) &&
                _setup.CapabilityLease.Bindings.TryGetValue(call.Name, out var binding) &&
                binding.Descriptor.Kind == ToolCallKind.Ask)
            {
                questionReturned = true;
                if (direct.Status == ToolCallStatus.Completed) _progressGuard.Reset();
            }
        }

        IReadOnlyList<ChatMessage> followUps = followUpImages.Count > 0
            ? [ToolResultImagePayload.CreateFollowUp(followUpImages)]
            : [];
        return (results, followUps);
    }

    private async Task<DirectToolResult> ExecuteToolAsync(
        FunctionCallContent[] calls, int index, CancellationToken cancellationToken)
    {
        var call = calls[index];
        try
        {
            return await _setup.Invoker.InvokeAsync(call, cancellationToken).ConfigureAwait(false);
        }
        // A checkpoint or input-session failure must close the batch; only the turn's token cancels it.
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _output.PublishToolFault(call.CallId, exception);
            CancelCalls(calls[(index + 1)..], DirectLoopMessages.BatchAbortedReason,
                DirectLoopMessages.BatchAbortedCall);
            throw;
        }
    }

    private async Task<ChatResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var usage = new RequestUsageAccumulator(_setup.ProviderLease.UsageUpdateKind);
        // A request that reports nothing must not leave the previous request's size standing in for its own.
        _lastRequestInputTokens = null;
        try
        {
            // ToChatResponseAsync aggregates incrementally, preserving native content and metadata.
            // It consumes through the END of the response, including calls emitted after finish_reason.
            return await TranslateResponseAsync(usage, cancellationToken)
                .ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Preserve observed usage even if the provider fails after reporting it. The progress snapshot
            // goes with it: one provider call is one point at which the displayed usage really moved.
            if (usage.Build() is { } observed)
            {
                _lastRequestInputTokens = observed.InputTokenCount is long input
                    ? (int)Math.Clamp(input, 0, int.MaxValue)
                    : null;
                _output.ObserveUsage(observed);
                _output.PublishUsageProgress();
            }
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> TranslateResponseAsync(
        RequestUsageAccumulator usage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in ProviderStreamIdleGuard.StreamAsync(
            _setup.ProviderLease.Client, _context.Messages, _setup.ProviderLease.Options,
            ProviderStreamIdleGuard.DefaultIdleTimeout, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents.OfType<UsageContent>()) usage.Observe(content.Details);
            _output.TranslateUpdate(update, _setup.CapabilityLease.Bindings);
            yield return update;
        }
    }
}
