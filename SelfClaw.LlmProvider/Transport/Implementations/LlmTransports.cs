using SelfClaw.LlmProvider.Transport.Abstractions;

namespace SelfClaw.LlmProvider.Transport.Implementations;

/// <summary>
/// 出站 transport 的入口。默认实现跟随系统代理并使用默认凭据；要自定义出口就实现
/// <see cref="ILlmTransport"/>，或把一个现成的 handler 交给 <see cref="FromHandler"/>。
/// </summary>
public static class LlmTransports
{
    /// <summary>跟随系统代理、默认凭据、连接池 5 分钟。无自定义头。</summary>
    public static ILlmTransport Default { get; } = new DefaultLlmTransport();

    /// <summary>
    /// 用一个现成 handler。本库只做 <c>new HttpClient(handler, disposeHandler: false)</c>，
    /// 从不 dispose 它，所以 handler 的连接池由调用方继续拥有。
    /// </summary>
    public static ILlmTransport FromHandler(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new FixedHandlerTransport(handler);
    }
}
