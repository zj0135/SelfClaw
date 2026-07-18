using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Shared secret-name constants and lookup rules for provider adapters and
/// model list clients, so the "missing api_key" contract stays identical
/// across providers.
/// </summary>
internal static class AiProviderSecrets
{
    internal const string ApiKeySecretName = "api_key";

    /// <summary>Returns the decrypted API key or throws the shared readable error.</summary>
    internal static string RequireApiKey(string connectionName, IReadOnlyDictionary<string, string> secrets)
        => secrets.TryGetValue(ApiKeySecretName, out var apiKey) && !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : throw new InvalidOperationException(
                $"AI provider connection '{connectionName}' is missing the required '{ApiKeySecretName}' secret.");

    /// <summary>
    /// Decrypts every credential ref on <paramref name="connection"/> into a name → plaintext map,
    /// skipping refs that resolve to blank. This is the shared retrieval loop; callers layer their own
    /// policy (auth-kind gating, required-key enforcement) on top.
    /// </summary>
    internal static async Task<Dictionary<string, string>> ResolveAsync(
        ISecretProtector secretProtector,
        AiProviderConnection connection,
        CancellationToken cancellationToken)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var credentialRef in connection.CredentialRefs)
        {
            if (string.IsNullOrWhiteSpace(credentialRef.Value))
            {
                continue;
            }

            var secret = await secretProtector.RetrieveSecretAsync(credentialRef.Value, cancellationToken);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                secrets[credentialRef.Key] = secret;
            }
        }

        return secrets;
    }
}
