using System.Text.Json;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

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
