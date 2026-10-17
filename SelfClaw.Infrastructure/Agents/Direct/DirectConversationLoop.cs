using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct;

internal static class DirectConversationLoop
{
    internal const int MaximumProviderRequestsPerTurn = 128;

    public static async Task<DirectLoopOutcome> RunAsync(DirectTurnSetup setup, DirectEventTranslator output,
        CancellationToken cancellationToken)
    {
        var context = new DirectConversationContext(setup.Messages);
        for (var requestIndex = 0; requestIndex < MaximumProviderRequestsPerTurn; requestIndex++)
        {
            var isLastRequest = requestIndex + 1 == MaximumProviderRequestsPerTurn;
            cancellationToken.ThrowIfCancellationRequested();
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

    private static async Task<DirectLoopOutcome?> ConsumeAvailableInputsAsync(DirectTurnSetup setup,
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
    private static async Task<DirectLoopOutcome?> TryConsumeBatchAsync(DirectTurnSetup setup, IDirectTurnInputSession inputs,
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
        context.AppendInputs(consumed.Messages);
        output.BeginInputSegment();
        return null;
    }

    /// <summary>
    /// The runtime budget guard shares the composer's UTF-8 heuristic. Only when the provider declares a
    /// context window is the batch refused before consumption; the already-consumed transcript is never
    /// trimmed to make room.
    /// </summary>
    private static bool FitsBudget(DirectTurnSetup setup, DirectConversationContext context, ConversationInputBatch batch)
    {
        if (AiChatOptions.ResolveContextWindowTokens(setup.ProviderLease.Profile) is not int contextWindow)
            return true;
        var reserve = setup.ProviderLease.Options.MaxOutputTokens ?? DirectPromptComposer.DefaultOutputTokenReserve;
        var used = DirectPromptComposer.EstimateMessages(context.Messages) +
                   DirectPromptComposer.EstimateTools(setup.ProviderLease.Options.Tools) + reserve;
        var batchTokens = batch.Inputs.Sum(input => DirectPromptComposer.EstimateUserPrompt(input.Prompt));
        return used + batchTokens <= contextWindow;
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
}
