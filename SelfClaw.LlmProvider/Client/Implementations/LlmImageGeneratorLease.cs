using Microsoft.Extensions.AI;

namespace SelfClaw.LlmProvider.Client.Implementations;

/// <summary>图像生成器的一次租约。释放它不影响调用方的共享 handler。</summary>
public sealed class LlmImageGeneratorLease : IDisposable
{
    private readonly HttpClient _httpClient;
    private int _disposed;

    internal LlmImageGeneratorLease(IImageGenerator generator, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(httpClient);
        Generator = generator;
        _httpClient = httpClient;
    }

    public IImageGenerator Generator { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            (Generator as IDisposable)?.Dispose();
        }
        finally
        {
            _httpClient.Dispose();
        }
    }
}
