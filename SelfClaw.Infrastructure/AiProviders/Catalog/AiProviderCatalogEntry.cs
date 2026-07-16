using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Catalog;

/// <summary>
/// Static metadata for a built-in provider option shown by the settings UI.
/// Connections reference an entry through <see cref="CatalogId"/>.
/// </summary>
public sealed record AiProviderCatalogEntry(
    string CatalogId,
    string DisplayName,
    string Subtitle,
    string AccentColor,
    AiProviderKind ProviderKind,
    Uri DefaultEndpoint,
    AiProviderApiFormat DefaultApiFormat,
    IReadOnlyList<AiProviderApiFormat> SupportedFormats,
    AiProviderAuthKind AuthKind,
    string? GetApiKeyUrl,
    bool SupportsModelListing,
    IReadOnlyList<AiModelDescriptor> WellKnownModels);
