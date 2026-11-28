using SelfClaw.LlmProvider.Transport.Abstractions;

namespace SelfClaw.LlmProvider.Transport.Implementations;

internal sealed class DefaultLlmTransport : ILlmTransport
{
    private readonly Lazy<HttpMessageHandler> _handler = new(Create);

    public HttpMessageHandler GetHandler() => _handler.Value;

    private static HttpMessageHandler Create() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseProxy = true,
        Proxy = null
    };
}
