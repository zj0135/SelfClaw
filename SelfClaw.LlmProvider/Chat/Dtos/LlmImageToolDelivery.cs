namespace SelfClaw.LlmProvider.Chat.Dtos;

/// <summary>某个协议把工具结果里的图片递给模型的方式。</summary>
public enum LlmImageToolDelivery
{
    /// <summary>该模型或协议不支持在工具结果里携带图片。</summary>
    Unsupported = 0,

    /// <summary>工具结果消息本身就带图片内容。</summary>
    Native = 1,

    /// <summary>图片必须作为紧随工具消息之后的独立用户消息发送。</summary>
    FollowUpUserMessage = 2
}
