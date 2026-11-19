using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Bounds how long a provider stream may stay silent. The turn's HttpClient has no timeout because a healthy
/// response can stream for many minutes; what marks a dead stream is the gap between two updates, so that gap
/// is what is limited. A background turn has nobody to press Esc, and without this a stalled provider would
/// hold it forever.
/// </summary>
internal static class ProviderStreamIdleGuard
{
    /// <summary>
    /// Far above the silence a slow reasoning model produces before its first token, so only a stream that
    /// really stopped trips it.
    /// </summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Streams one provider response and throws <see cref="TimeoutException"/> once no update arrived for
    /// <paramref name="idleTimeout"/>. The timeout is a failure, never a cancellation: only the caller's own
    /// token may end the stream with <see cref="OperationCanceledException"/>.
    /// </summary>
    internal static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        TimeSpan idleTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(messages);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = client.GetStreamingResponseAsync(messages, options, idle.Token)
            .GetAsyncEnumerator(idle.Token);
        while (true)
        {
            idle.CancelAfter(idleTimeout);
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The provider sent no data for {idleTimeout.TotalSeconds:0} seconds, so the request was abandoned.");
            }

            if (!hasNext)
            {
                yield break;
            }

            // The consumer's own work between two updates is not provider silence.
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            yield return enumerator.Current;
        }
    }
}
