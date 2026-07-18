using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using OpenAIChatClient = OpenAI.Chat.ChatClient;
using OpenAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;

namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

/// <summary>
/// OpenAI Chat Completions format support. Creates an <see cref="OpenAIChatClient"/>
/// against the connection endpoint and maps model-specific options onto raw
/// <see cref="OpenAIChatCompletionOptions"/> via JSON patches, preserving the
/// project's existing default of writing <c>$.thinking.type</c>.
/// </summary>
internal sealed partial class OpenAiProviderAdapter
{
    private const string ChatThinkingTypeKey = "thinking.type";
    private const string ChatReasoningEffortKey = "reasoning_effort";
    private const string ChatParallelToolCallsKey = "parallel_tool_calls";
    private const string ChatStoreKey = "store";

    private static readonly IReadOnlySet<string> ChatCompletionRecognizedKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ChatThinkingTypeKey,
            ChatReasoningEffortKey,
            ChatParallelToolCallsKey,
            ChatStoreKey
        };

    private IChatClient CreateChatCompletionsClient(AiProviderClientRequest request)
    {
        var client = new OpenAIChatClient(
            request.Profile.Model,
            CreateCredential(request),
            CreateClientOptions(request.Connection));
        return client.AsIChatClient();
    }

    private ChatOptions CreateChatCompletionsOptions(AiProviderClientRequest request) =>
        AiChatOptions.CreateBase(request, _ => BuildChatCompletionRawOptions(request));

    private OpenAIChatCompletionOptions BuildChatCompletionRawOptions(AiProviderClientRequest request)
    {
#pragma warning disable SCME0001
        var raw = new OpenAIChatCompletionOptions();
        var options = OptionReader(request);

        // thinking.type is a non-standard parameter that the strict OpenAI
        // endpoint rejects, so only the OpenAI-compatible kind gets an
        // EnableReasoning-derived default. An explicit option always wins, and
        // strict OpenAI emits thinking.type only when it is explicitly configured.
        if (options.TryReadString(ChatThinkingTypeKey, out var explicitThinking))
        {
            raw.Patch.Set("$.thinking.type"u8, explicitThinking);
        }
        else if (_providerKind == AiProviderKind.OpenAICompatible)
        {
            raw.Patch.Set("$.thinking.type"u8, request.EnableReasoning ? "enabled" : "disabled");
        }

        if (options.TryReadString(ChatReasoningEffortKey, out var reasoningEffort))
        {
            raw.Patch.Set("$.reasoning_effort"u8, reasoningEffort);
        }

        if (options.TryReadBool(ChatParallelToolCallsKey, out var parallelToolCalls))
        {
            raw.Patch.Set("$.parallel_tool_calls"u8, parallelToolCalls);
        }

        if (options.TryReadBool(ChatStoreKey, out var store))
        {
            raw.Patch.Set("$.store"u8, store);
        }

        options.LogUnknown(ChatCompletionRecognizedKeys);
        return raw;
#pragma warning restore SCME0001
    }
}
