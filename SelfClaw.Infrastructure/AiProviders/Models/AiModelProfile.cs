using System.Text.Json;

namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// A concrete model configuration bound to a provider connection. Carries the
/// wire format, model id, shared sampling options and model/format-scoped
/// options (e.g. reasoning, store, max output tokens, parallel tool calls).
/// </summary>
/// <remarks>
/// <see cref="ApiFormat"/> chooses between OpenAI Chat Completions and the
/// OpenAI Responses API. <see cref="ModelOptions"/> is an open bag interpreted
/// per adapter and per <see cref="ApiFormat"/>; unrecognized keys are ignored.
/// </remarks>
public sealed record AiModelProfile(
    Guid Id,
    Guid ProviderConnectionId,
    string Name,
    AiProviderApiFormat ApiFormat,
    string Model,
    AiSamplingOptions Sampling,
    IReadOnlyDictionary<string, JsonElement> ModelOptions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsEnabled = true);
