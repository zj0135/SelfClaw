using System.Text.Json;

namespace SelfClaw.Core.Models;

public sealed record SaveProviderCommand(
    Guid? Id,
    string CatalogId,
    string Name,
    Uri Endpoint,
    string? ApiKey,
    IReadOnlyDictionary<string, JsonElement>? ConnectionOptions,
    AiProviderKind? ProviderKind = null,
    AiProviderApiFormat? DefaultApiFormat = null,
    bool? Enabled = null);
