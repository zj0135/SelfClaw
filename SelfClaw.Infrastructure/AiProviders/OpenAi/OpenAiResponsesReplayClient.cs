using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001
namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

// The SDK stream parser has the completed native item, but drops it on FunctionCallContent.
// Retain it so replay includes its item id/status as well as the function call id.
internal sealed class OpenAiResponsesReplayClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (update.RawRepresentation is StreamingResponseOutputItemDoneUpdate { Item: FunctionCallResponseItem item })
            {
                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                    if (call.CallId == item.CallId) call.RawRepresentation = item;
            }
            yield return update;
        }
    }
}
