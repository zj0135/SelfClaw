using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace SelfClaw.Infrastructure.AiProviders.OpenAi;

// M.E.AI 10.10 reads reasoning_content but omits it when writing assistant messages.
// Use its public native converter, then patch only the missing reasoning field.
internal sealed class OpenAiChatReasoningReplayClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(WithReasoning(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(WithReasoning(messages), options, cancellationToken);

    private static IEnumerable<Microsoft.Extensions.AI.ChatMessage> WithReasoning(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> history)
    {
        foreach (var message in history)
        {
            var reasoning = message.Role == ChatRole.Assistant
                ? string.Concat(message.Contents.OfType<TextReasoningContent>().Select(content => content.Text))
                : string.Empty;
            if (reasoning.Length == 0)
            {
                yield return message;
                continue;
            }

            var native = new[] { message }.AsOpenAIChatMessages().Single();
#pragma warning disable SCME0001
            native.Patch.Set("$.reasoning_content"u8, reasoning);
#pragma warning restore SCME0001
            var replay = message.Clone();
            replay.RawRepresentation = native;
            yield return replay;
        }
    }
}
