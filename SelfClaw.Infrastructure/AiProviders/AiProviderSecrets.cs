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
}
