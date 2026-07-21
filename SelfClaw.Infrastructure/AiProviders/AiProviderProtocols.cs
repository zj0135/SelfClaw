using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.Models.Views;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Curated protocol choices offered when creating a custom provider connection,
/// plus the derivations shared by the settings service. A protocol option is
/// what the user picks in the "add custom provider" dialog; it maps to the
/// concrete <see cref="AiProviderKind"/> and default
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
                SupportsModelListing: true),
            new AiProviderProtocolOption(
                "ollama",
                "Ollama 原生",
                AiProviderKind.Ollama,
                AiProviderApiFormat.OllamaNative,
                AiProviderAuthKind.None,
                SupportsModelListing: true)
        ]);

    /// <summary>The protocol options shown when adding a custom provider connection.</summary>
    public static IReadOnlyList<AiProviderProtocolOption> CustomProtocolOptions => CustomOptions;

    /// <summary>
    /// Derives the authentication scheme for a provider kind. Only local Ollama
    /// runs without an API key; every other kind uses an API key.
    /// </summary>
    public static AiProviderAuthKind ResolveAuthKind(AiProviderKind providerKind)
        => providerKind == AiProviderKind.Ollama
            ? AiProviderAuthKind.None
            : AiProviderAuthKind.ApiKey;
}
