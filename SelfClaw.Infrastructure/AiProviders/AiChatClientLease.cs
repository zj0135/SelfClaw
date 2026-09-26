using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Owns the per-turn chat pipeline. Disposing the lease releases the pipeline, the provider SDK client,
/// and the per-turn <see cref="HttpClient"/>; the shared pooled handler behind that client remains owned
/// by <c>AiProviderHttpClientProvider</c>.
/// </summary>
public sealed class AiChatClientLease : IDisposable
{
    private int _disposed;

    public AiChatClientLease(
        IChatClient client,
        ChatOptions options,
        AiModelProfile profile,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(httpClient);
        Client = client;
        Options = options;
        Profile = profile;
        HttpClient = httpClient;
    }

    public IChatClient Client { get; }
    public ChatOptions Options { get; }
    public AiModelProfile Profile { get; }
    public HttpClient HttpClient { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The pooled handler lives behind the turn client, so it must be released even when
        // disposing the pipeline throws.
        try
        {
            Client.Dispose();
        }
        finally
        {
            HttpClient.Dispose();
        }
    }
}
