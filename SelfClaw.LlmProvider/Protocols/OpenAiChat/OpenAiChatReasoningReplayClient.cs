using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SelfClaw.LlmProvider.Protocols.OpenAiChat;

/// <summary>
/// M.E.AI 读取 <c>reasoning_content</c> 但在写出助手消息时会丢掉它。用 SDK 的官方转换器
/// 造出原生消息，只把缺失的那个字段补回去，思考内容才能在下一回合被重放。
/// </summary>
internal sealed class OpenAiChatReasoningReplayClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(WithReasoning(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(WithReasoning(messages), options, cancellationToken);

    private static IEnumerable<ChatMessage> WithReasoning(IEnumerable<ChatMessage> history)
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
