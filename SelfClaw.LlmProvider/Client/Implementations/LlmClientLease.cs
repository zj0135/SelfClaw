using Microsoft.Extensions.AI;


namespace SelfClaw.LlmProvider.Client.Implementations;

/// <summary>
/// 一回合的聊天管道。释放它会释放客户端与这层薄 HttpClient；调用方交给
/// <see cref="ILlmTransport"/> 的共享 handler 不受影响。
/// </summary>
public sealed class LlmClientLease : IDisposable
{
    private readonly HttpClient _httpClient;
    private int _disposed;

    internal LlmClientLease(IChatClient client, ChatOptions options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        Client = client;
        Options = options;
        _httpClient = httpClient;
    }

    public IChatClient Client { get; }

    public ChatOptions Options { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 池化的 handler 在这层 client 之后，所以即使释放管道抛错也必须释放它。
        try
        {
            Client.Dispose();
        }
        finally
        {
            _httpClient.Dispose();
        }
    }
}
