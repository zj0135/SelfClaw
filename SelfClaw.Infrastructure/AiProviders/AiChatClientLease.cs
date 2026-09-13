using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Owns the per-turn chat pipeline. Disposing the lease releases the pipeline
/// and provider SDK client; shared cached HttpClient instances remain owned by
/// <c>AiProviderHttpClientProvider</c>.
/// </summary>
public sealed class AiChatClientLease : IDisposable
{
    private int _disposed;

    public AiChatClientLease(IChatClient client, ChatOptions options, AiModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        Client = client;
        Options = options;
        Profile = profile;
    }

    public IChatClient Client { get; }
    public ChatOptions Options { get; }
    public AiModelProfile Profile { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Client.Dispose();
        }
    }
}
