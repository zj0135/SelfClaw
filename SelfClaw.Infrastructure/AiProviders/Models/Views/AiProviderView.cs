using System.Text.Json;

namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

public sealed record AiProviderView(
    Guid? ConnectionId,
    string CatalogId,
    string Name,
    string Sub,
    string Color,
    bool Enabled,
    bool IsConfigured,
    bool HasApiKey,
    string? KeyMask,
    string Base,
    AiProviderKind ProviderKind,
    AiProviderAuthKind AuthKind,
    string? GetApiKeyUrl,
    bool SupportsModelListing,
    AiProviderApiFormat DefaultApiFormat,
    IReadOnlyList<AiProviderApiFormat> SupportedFormats,
    IReadOnlyDictionary<string, JsonElement> ConnectionOptions,
    IReadOnlyList<AiModelView> Models,
    int Total);
