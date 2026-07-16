using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Owns the per-turn chat pipeline. Disposing the lease releases the pipeline
/// and provider SDK client; shared cached HttpClient instances remain owned by
/// <c>AiProviderHttpClientProvider</c>.
/// </summary>
public sealed record AiChatClientLease(
    IChatClient Client,
    ChatOptions Options,
    AiModelProfile Profile) : IDisposable
{
    public void Dispose() => Client.Dispose();
}
