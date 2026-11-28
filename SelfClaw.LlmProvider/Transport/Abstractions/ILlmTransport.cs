namespace SelfClaw.LlmProvider.Transport.Abstractions;

/// <summary>
/// 出站 HTTP 的口子。调用方决定代理、连接池、证书、测试替身；本库只包装它返回的 handler。
/// </summary>
/// <remarks>
/// 为什么是 handler 而不是 HttpClient：官方 SDK 会 dispose 传进去的 HttpClient
/// （Anthropic 的 ClientOptions.DisposeHttpResources 明确如此），且共享实例上的 Timeout、
/// BaseAddress、DefaultRequestHeaders 都是每连接不许改的。handler 才是真正持有连接池和代理配置的对象。
/// 库只做 <c>new HttpClient(handler, disposeHandler: false)</c>，从不 dispose 这个 handler。
/// </remarks>
public interface ILlmTransport
{
    /// <summary>
    /// 返回当前出口的共享 handler。实现方拥有它的生命周期；本库每次调用都拿它造一层薄 HttpClient。
    /// </summary>
    HttpMessageHandler GetHandler();
}
