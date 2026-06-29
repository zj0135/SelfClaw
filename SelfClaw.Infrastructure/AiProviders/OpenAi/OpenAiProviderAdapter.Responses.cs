using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using OpenAIResponsesClient = OpenAI.Responses.ResponsesClient;
using OpenAICreateResponseOptions = OpenAI.Responses.CreateResponseOptions;
using OpenAIResponseReasoningOptions = OpenAI.Responses.ResponseReasoningOptions;
using OpenAIResponseReasoningEffortLevel = OpenAI.Responses.ResponseReasoningEffortLevel;
using OpenAIResponseReasoningSummaryVerbosity = OpenAI.Responses.ResponseReasoningSummaryVerbosity;
using OpenAIResponseTruncationMode = OpenAI.Responses.ResponseTruncationMode;

// This partial targets the OpenAI Responses API, which the SDK still ships as an
// evaluation-only (experimental) surface. Suppress OPENAI001 for the whole file.
#pragma warning disable OPENAI001

namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

/// <summary>
/// OpenAI Responses API format support. Creates an <see cref="OpenAIResponsesClient"/>
/// and exposes it through Microsoft Agent Framework via
/// <c>AsIChatClient(model)</c>, mapping model-specific options onto a strongly
/// typed <see cref="OpenAICreateResponseOptions"/>. Reasoning is controlled only
/// by explicit model options here; it is never derived from
/// <see cref="AiProviderClientRequest.EnableReasoning"/>.
/// </summary>
internal sealed partial class OpenAiProviderAdapter
{
    private const string ResponseReasoningEffortKey = "reasoning.effort";
    private const string ResponseReasoningSummaryKey = "reasoning.summary";
    private const string ResponseStoreKey = "store";
    private const string ResponseMaxOutputTokensKey = "max_output_tokens";
    private const string ResponseTruncationKey = "truncation";
    private const string ResponseParallelToolCallsKey = "parallel_tool_calls";

    private static readonly IReadOnlySet<string> ResponseRecognizedKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ResponseReasoningEffortKey,
            ResponseReasoningSummaryKey,
            ResponseStoreKey,
            ResponseMaxOutputTokensKey,
            ResponseTruncationKey,
            ResponseParallelToolCallsKey
        };

    private IChatClient CreateResponsesClient(AiProviderClientRequest request)
    {
        var client = new OpenAIResponsesClient(
            CreateCredential(request),
            CreateResponsesClientOptions(request.Connection));
        return client.AsIChatClient(request.Profile.Model);
    }

    private ChatOptions CreateResponsesOptions(AiProviderClientRequest request) =>
        CreateSharedChatOptions(request, _ => BuildResponseRawOptions(request));

    private OpenAICreateResponseOptions BuildResponseRawOptions(AiProviderClientRequest request)
    {
        var raw = new OpenAICreateResponseOptions();
        var options = request.Profile.ModelOptions;
        var profileName = request.Profile.Name;

        // Reasoning is explicit-only for Responses; do not inject anything from
        // EnableReasoning so unsupported models are not handed invalid params.
        OpenAIResponseReasoningOptions? reasoning = null;
        if (TryReadString(options, ResponseReasoningEffortKey, profileName, out var effort))
        {
            reasoning ??= new OpenAIResponseReasoningOptions();
            reasoning.ReasoningEffortLevel = new OpenAIResponseReasoningEffortLevel(effort);
        }

        if (TryReadString(options, ResponseReasoningSummaryKey, profileName, out var summary))
        {
            reasoning ??= new OpenAIResponseReasoningOptions();
            reasoning.ReasoningSummaryVerbosity = new OpenAIResponseReasoningSummaryVerbosity(summary);
        }

        if (reasoning is not null)
        {
            raw.ReasoningOptions = reasoning;
        }

        if (TryReadBool(options, ResponseStoreKey, profileName, out var store))
        {
            raw.StoredOutputEnabled = store;
        }

        if (TryReadInt(options, ResponseMaxOutputTokensKey, profileName, out var maxOutputTokens))
        {
            raw.MaxOutputTokenCount = maxOutputTokens;
        }

        if (TryReadString(options, ResponseTruncationKey, profileName, out var truncation))
        {
            raw.TruncationMode = new OpenAIResponseTruncationMode(truncation);
        }

        if (TryReadBool(options, ResponseParallelToolCallsKey, profileName, out var parallelToolCalls))
        {
            raw.ParallelToolCallsEnabled = parallelToolCalls;
        }

        LogUnknownOptions(options, ResponseRecognizedKeys, profileName);
        return raw;
    }
}
