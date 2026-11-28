using SelfClaw.LlmProvider.Transport.Abstractions;

namespace SelfClaw.LlmProvider.Transport.Implementations;

internal sealed class FixedHandlerTransport : ILlmTransport
{
    private readonly HttpMessageHandler _handler;

    internal FixedHandlerTransport(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpMessageHandler GetHandler() => _handler;
}
