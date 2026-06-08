using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

/// <summary>
/// Resolved input handed to an <c>IAiProviderAdapter</c> to build an
/// <see cref="IChatClient"/> and its <see cref="ChatOptions"/>. Combines the
/// connection, model profile, decrypted secrets and per-turn runtime inputs.
/// </summary>
/// <remarks>
/// <see cref="Secrets"/> holds already-decrypted values keyed by credential
/// name; the OpenAI v1 adapter reads <c>api_key</c>. <see cref="EnableReasoning"/>
/// only influences the Chat Completions reasoning default and is intentionally
/// ignored by the Responses format.
/// </remarks>
public sealed record AiProviderClientRequest(
    AiProviderConnection Connection,
    AiModelProfile Profile,
    IReadOnlyDictionary<string, string> Secrets,
    bool EnableReasoning,
    IReadOnlyList<AITool> Tools);
