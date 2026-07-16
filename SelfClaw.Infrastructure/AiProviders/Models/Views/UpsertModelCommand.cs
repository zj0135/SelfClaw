using System.Text.Json;

namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

public sealed record UpsertModelCommand(
    Guid? Id,
    Guid ProviderConnectionId,
    string Name,
    AiProviderApiFormat ApiFormat,
    string Model,
    AiSamplingOptions? Sampling,
    IReadOnlyDictionary<string, JsonElement>? ModelOptions,
    bool? Enabled = null);
