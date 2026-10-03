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

internal sealed class DirectConversationLoop
{
    internal const int MaximumProviderRequestsPerTurn = 128;

    public async Task<DirectLoopOutcome> RunAsync(DirectTurnSetup.Ready setup, DirectEventTranslator output,
        CancellationToken cancellationToken)
    {
        var context = new DirectConversationContext(setup.Messages);
        for (var requestIndex = 0; requestIndex < MaximumProviderRequestsPerTurn; requestIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ConsumeAvailableInputsAsync(setup.InputSession, context, output, cancellationToken).ConfigureAwait(false);
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
                var batch = setup.InputSession is { } inputs
                    ? await inputs.ReadBoundaryAsync(cancellationToken).ConfigureAwait(false)
                    : null;
                if (batch is null) return DirectLoopOutcome.Completed;
                if (requestIndex + 1 == MaximumProviderRequestsPerTurn) return DirectLoopOutcome.BudgetExhausted;
                await ConsumeInputAsync(setup.InputSession ?? throw new InvalidOperationException("Missing turn input session."),
                    batch, context, output, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (requestIndex + 1 == MaximumProviderRequestsPerTurn)
            {
                CancelCalls(setup, output, calls, "BudgetExhausted",
                    "The turn reached its 128 provider request limit before this tool could execute.");
                return DirectLoopOutcome.BudgetExhausted;
            }

            context.AppendResults(await ExecuteToolsAsync(setup, output, calls, requestIndex, cancellationToken).ConfigureAwait(false));
        }
        throw new InvalidOperationException("The provider request budget was not resolved.");
    }

    private static async Task ConsumeAvailableInputsAsync(IDirectTurnInputSession? inputs,
        DirectConversationContext context, DirectEventTranslator output, CancellationToken cancellationToken)
    {
        if (inputs is null) return;
        while (await inputs.ReadBoundaryAsync(cancellationToken).ConfigureAwait(false) is { } batch)
            await ConsumeInputAsync(inputs, batch, context, output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConsumeInputAsync(IDirectTurnInputSession inputs, ConversationInputBatch batch,
        DirectConversationContext context, DirectEventTranslator output, CancellationToken cancellationToken)
    {
        output.PublishInputBoundary(batch);
        var consumed = await inputs.WaitForCommitAsync(batch, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        context.AppendInputs(consumed.Messages);
        output.BeginInputSegment();
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

    private static void CancelCalls(DirectTurnSetup.Ready setup, DirectEventTranslator output,
        IEnumerable<FunctionCallContent> calls, string reason, string detail)
    {
        foreach (var call in calls.DistinctBy(call => call.CallId))
            PublishResult(setup, output, new FunctionResultContent(call.CallId, new DirectToolResult(
                ToolCallStatus.Canceled, reason, JsonSerializer.SerializeToElement(new { reason }), detail)));
    }

    private static async Task<IList<AIContent>> ExecuteToolsAsync(DirectTurnSetup.Ready setup,
        DirectEventTranslator output, FunctionCallContent[] calls, int requestIndex, CancellationToken cancellationToken)
    {
        var results = new List<AIContent>();
        foreach (var call in calls)
        {
            FunctionResultContent result;
            try
            {
                result = new FunctionResultContent(call.CallId,
                    await setup.Invoker.InvokeAsync(call, requestIndex, cancellationToken).ConfigureAwait(false));
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

    private static async Task<ChatResponse> ReadResponseAsync(DirectTurnSetup.Ready setup,
        DirectConversationContext context, DirectEventTranslator output, CancellationToken cancellationToken)
    {
        var usage = new RequestUsageAccumulator(setup.ProviderLease.UsageUpdateKind);
        try
        {
            // ToChatResponseAsync aggregates incrementally, preserving native content and metadata.
            // It consumes through the END of the response, including calls emitted after finish_reason.
            var response = await TranslateResponseAsync(setup, context, output, usage, cancellationToken)
                .ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
            response.Usage = usage.Build();
            return response;
        }
        finally
        {
            // Preserve observed usage even if the provider fails after reporting it.
            if (usage.Build() is { } observed) output.ObserveUsage(observed);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TranslateResponseAsync(DirectTurnSetup.Ready setup,
        DirectConversationContext context, DirectEventTranslator output, RequestUsageAccumulator usage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in setup.ProviderLease.Client.GetStreamingResponseAsync(
            context.Messages, setup.ProviderLease.Options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents.OfType<UsageContent>()) usage.Observe(content.Details);
            output.TranslateUpdate(update, setup.CapabilityLease.Bindings, setup.Invoker);
            yield return update;
        }
    }

    private static void PublishResult(DirectTurnSetup.Ready setup, DirectEventTranslator output, FunctionResultContent result)
        => output.TranslateUpdate(new ChatResponseUpdate(ChatRole.Tool, [result]), setup.CapabilityLease.Bindings, setup.Invoker);
}
