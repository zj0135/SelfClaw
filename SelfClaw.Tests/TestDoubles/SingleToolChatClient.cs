using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class SingleToolChatClient : IChatClient
{
    private readonly string _toolName;
    private readonly IDictionary<string, object?> _arguments;
    private readonly Func<CancellationToken, Task>? _afterTool;
    private int _requests;

    internal SingleToolChatClient(string toolName, IDictionary<string, object?> arguments,
        Func<CancellationToken, Task>? afterTool = null)
    {
        _toolName = toolName;
        _arguments = arguments;
        _afterTool = afterTool;
    }

    internal object? ToolResult { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _requests) == 1)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", _toolName, _arguments)])
            {
                MessageId = "call-message", FinishReason = ChatFinishReason.ToolCalls
            };
            yield break;
        }

        ToolResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Last().Result;
        if (_afterTool is not null)
        {
            await _afterTool(cancellationToken);
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
        {
            MessageId = "answer-message", FinishReason = ChatFinishReason.Stop
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
