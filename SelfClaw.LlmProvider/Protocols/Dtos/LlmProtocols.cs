namespace SelfClaw.LlmProvider.Protocols.Dtos;

/// <summary>内置协议 id。调用方也可以注册自己的协议 id。</summary>
public static class LlmProtocols
{
    public const string OpenAiChat = "openai-chat";
    public const string OpenAiResponses = "openai-responses";
    public const string AnthropicMessages = "anthropic-messages";
    public const string OpenAiImages = "openai-images";
    public const string OpenAiEmbeddings = "openai-embeddings";

    public static IReadOnlyList<string> BuiltIn { get; } =
    [
        OpenAiChat,
        OpenAiResponses,
        AnthropicMessages,
        OpenAiImages,
        OpenAiEmbeddings
    ];

    public static bool IsChat(string protocol) =>
        protocol is OpenAiChat or OpenAiResponses or AnthropicMessages;

    public static bool IsImage(string protocol) => protocol is OpenAiImages;

    public static bool IsEmbedding(string protocol) => protocol is OpenAiEmbeddings;
}
