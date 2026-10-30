using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Curated protocol choices offered when creating a custom provider connection.
/// A protocol option is what the user picks in the "add custom provider" dialog;
/// it maps to the concrete <see cref="AiProviderKind"/> and default
/// <see cref="AiProviderApiFormat"/> that back the connection.
/// </summary>
public static class AiProviderProtocols
{
    /// <summary>Connection option key that stores the default wire format for a connection.</summary>
    public const string DefaultApiFormatOptionKey = "default_api_format";

    private static readonly IReadOnlyList<AiProviderProtocolOption> CustomOptions =
        Array.AsReadOnly(
        [
            new AiProviderProtocolOption(
                "openai-chat",
                "OpenAI Chat Completions 兼容",
                AiProviderKind.OpenAICompatible,
                AiProviderApiFormat.OpenAIChatCompletions,
                AiProviderAuthKind.ApiKey,
                SupportsModelListing: true),
            new AiProviderProtocolOption(
                "openai-responses",
                "OpenAI Responses 兼容",
                AiProviderKind.OpenAICompatible,
                AiProviderApiFormat.OpenAIResponses,
                AiProviderAuthKind.ApiKey,
                SupportsModelListing: true),
            new AiProviderProtocolOption(
                "anthropic",
                "Anthropic 协议",
                AiProviderKind.Anthropic,
                AiProviderApiFormat.AnthropicMessages,
                AiProviderAuthKind.ApiKey,
                SupportsModelListing: true)
        ]);

    /// <summary>The protocol options shown when adding a custom provider connection.</summary>
    public static IReadOnlyList<AiProviderProtocolOption> CustomProtocolOptions => CustomOptions;
}
