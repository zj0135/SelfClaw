using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001

namespace SelfClaw.LlmProvider.Protocols.OpenAiResponses;

/// <summary>
/// SDK 的流解析器手里有完整的原生 item，却在 <see cref="FunctionCallContent"/> 上把它丢掉了。
/// 留住它，重放时才能带上 item id/status 与 call id。
/// </summary>
internal sealed class OpenAiResponsesReplayClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base
                           .GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (update.RawRepresentation is StreamingResponseOutputItemDoneUpdate { Item: FunctionCallResponseItem item })
            {
                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                {
                    if (call.CallId == item.CallId)
                    {
                        call.RawRepresentation = item;
                    }
                }
            }

            yield return update;
        }
    }
}

#pragma warning restore OPENAI001
