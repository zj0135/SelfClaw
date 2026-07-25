using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Catalog;

/// <summary>
/// Built-in provider metadata. This is product configuration, not user data,
/// and therefore remains a static source rather than being persisted.
/// </summary>
public static class AiProviderCatalog
{
    private const string CustomCatalogId = "custom";

    private static readonly IReadOnlyList<AiModelDescriptor> NoWellKnownModels =
        Array.Empty<AiModelDescriptor>();

    private static readonly IReadOnlyList<AiProviderCatalogEntry> CatalogEntries =
        Array.AsReadOnly(
        [
            Entry(
                "openai",
                "OpenAI",
                "Responses and Chat Completions",
                "#10A37F",
                AiProviderKind.OpenAI,
                "https://api.openai.com/v1/",
                AiProviderApiFormat.OpenAIResponses,
                [AiProviderApiFormat.OpenAIChatCompletions, AiProviderApiFormat.OpenAIResponses],
                AiProviderAuthKind.ApiKey,
                "https://platform.openai.com/api-keys",
                supportsModelListing: true),
            Entry(
                "anthropic",
                "Anthropic",
                "Claude Messages API",
                "#C9682A",
                AiProviderKind.Anthropic,
                "https://api.anthropic.com/",
                AiProviderApiFormat.AnthropicMessages,
                [AiProviderApiFormat.AnthropicMessages],
                AiProviderAuthKind.ApiKey,
                "https://console.anthropic.com/settings/keys",
                supportsModelListing: true),
            Entry(
                "ollama",
                "Ollama",
                "Local model runtime",
                "#3A3A3A",
                AiProviderKind.Ollama,
                "http://localhost:11434/",
                AiProviderApiFormat.OllamaNative,
                [AiProviderApiFormat.OllamaNative, AiProviderApiFormat.OpenAIChatCompletions],
                AiProviderAuthKind.None,
                getApiKeyUrl: null,
                supportsModelListing: true),
            Entry(
                CustomCatalogId,
                "自定义",
                "OpenAI-compatible custom endpoint",
                "#6B7280",
                AiProviderKind.OpenAICompatible,
                "https://api.example.com/v1/",
                AiProviderApiFormat.OpenAIChatCompletions,
                [AiProviderApiFormat.OpenAIChatCompletions, AiProviderApiFormat.OpenAIResponses],
                AiProviderAuthKind.ApiKey,
                getApiKeyUrl: null,
                supportsModelListing: true)
        ]);

    private static readonly IReadOnlyDictionary<string, AiProviderCatalogEntry> EntriesById =
        CatalogEntries.ToDictionary(entry => entry.CatalogId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AiProviderCatalogEntry> Entries => CatalogEntries;

    public static AiProviderCatalogEntry GetRequired(string catalogId)
    {
        if (!string.IsNullOrWhiteSpace(catalogId) && EntriesById.TryGetValue(catalogId, out var entry))
        {
            return entry;
        }

        return EntriesById[CustomCatalogId];
    }

    private static AiProviderCatalogEntry Entry(
        string catalogId,
        string displayName,
        string subtitle,
        string accentColor,
        AiProviderKind providerKind,
        string defaultEndpoint,
        AiProviderApiFormat defaultApiFormat,
        AiProviderApiFormat[] supportedFormats,
        AiProviderAuthKind authKind,
        string? getApiKeyUrl,
        bool supportsModelListing)
        => new(
            catalogId,
            displayName,
            subtitle,
            accentColor,
            providerKind,
            new Uri(defaultEndpoint, UriKind.Absolute),
            defaultApiFormat,
            Array.AsReadOnly(supportedFormats),
            authKind,
            getApiKeyUrl,
            supportsModelListing,
            NoWellKnownModels);
}
