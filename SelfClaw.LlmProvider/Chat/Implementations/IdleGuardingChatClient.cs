using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SelfClaw.LlmProvider.Chat.Implementations;

/// <summary>
/// 限制 provider 流可以静默多久。回合的 HttpClient 不设超时，因为健康的长回答可以流很久；
/// 真正标记"流已经死了"的是两次更新之间的间隔，所以限制的是这个间隔。
/// 超时是失败，永远不是取消：只有调用方自己的 token 才能以取消结束流。
/// </summary>
internal sealed class IdleGuardingChatClient : DelegatingChatClient
{
    private readonly TimeSpan _idleTimeout;

    internal IdleGuardingChatClient(IChatClient innerClient, TimeSpan idleTimeout)
        : base(innerClient)
    {
        _idleTimeout = idleTimeout;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = base
            .GetStreamingResponseAsync(messages, options, idle.Token)
            .GetAsyncEnumerator(idle.Token);

        while (true)
        {
            idle.CancelAfter(_idleTimeout);
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The provider sent no data for {_idleTimeout.TotalSeconds:0} seconds, so the request was abandoned.");
            }

            if (!hasNext)
            {
                yield break;
            }

            // 消费者自己处理上一个更新的时间不算 provider 静默。
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            yield return enumerator.Current;
        }
    }
}
