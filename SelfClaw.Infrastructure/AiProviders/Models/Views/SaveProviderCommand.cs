using System.Text.Json;

namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

public sealed record SaveProviderCommand(
    Guid? Id,
    string CatalogId,
    string Name,
    Uri Endpoint,
    string? ApiKey,
    IReadOnlyDictionary<string, JsonElement>? ConnectionOptions);
