namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Resolved input handed to an <c>IAiProviderAdapter</c> to build an
/// <see cref="IChatClient"/> and its <see cref="ChatOptions"/>. Combines the
/// connection, model profile, decrypted secrets and per-turn runtime inputs.
/// </summary>
/// <remarks>
/// <see cref="Secrets"/> holds already-decrypted values keyed by credential
/// name; the OpenAI v1 adapter reads <c>api_key</c>. <see cref="EnableReasoning"/>
/// only influences the Chat Completions reasoning default and is intentionally
/// ignored by the Responses format. The turn's tools are not part of this record:
/// they are passed to <c>CreateChatOptions</c> so a preparation can never look like
/// it carries a tool set it does not have.
/// </remarks>
public sealed record AiProviderClientRequest(
    AiProviderConnection Connection,
    AiModelProfile Profile,
    IReadOnlyDictionary<string, string> Secrets,
    bool EnableReasoning);
