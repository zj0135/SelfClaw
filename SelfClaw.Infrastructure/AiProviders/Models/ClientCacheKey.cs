namespace SelfClaw.Infrastructure.AiProviders.Models;

internal readonly record struct ClientCacheKey(string Fingerprint, bool Streaming);
