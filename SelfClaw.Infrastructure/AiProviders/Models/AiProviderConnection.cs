using System.Text.Json;

namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Provider-level connection: endpoint, auth scheme, credential references and
/// connection-scoped options (e.g. organization, project, timeout, proxy). One
/// connection can back many <see cref="AiModelProfile"/> rows.
/// </summary>
/// <remarks>
/// <see cref="CredentialRefs"/> holds secret references only (e.g.
/// <c>api_key</c> -> secret ref); plaintext secrets are never stored here and
/// are resolved at request time into <see cref="AiProviderClientRequest.Secrets"/>.
/// </remarks>
public sealed record AiProviderConnection(
    Guid Id,
    string CatalogId,
    string Name,
    AiProviderKind ProviderKind,
    Uri Endpoint,
    AiProviderAuthKind AuthKind,
    IReadOnlyDictionary<string, string> CredentialRefs,
    IReadOnlyDictionary<string, JsonElement> ConnectionOptions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsEnabled = true);
