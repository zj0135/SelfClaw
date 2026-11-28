using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Protocols.Abstractions;

using SelfClaw.LlmProvider.Protocols.Dtos;

namespace SelfClaw.LlmProvider.Chat.Dtos;

/// <summary>投递方式加上不可用原因，避免调用方去匹配错误文案。</summary>
public sealed record LlmImageToolCapability(LlmImageToolDelivery Delivery, string? UnavailableReason = null)
{
    /// <summary>
    /// 模型未声明多模态时不投递；协议再决定形状。未验证的协议保持不支持，
    /// 因为猜错会把 base64 当成一个巨大 JSON 字符串发给对方。
    /// </summary>
    public static LlmImageToolCapability Resolve(LlmModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Limits.IsMultimodal)
        {
            return new LlmImageToolCapability(
                LlmImageToolDelivery.Unsupported,
                "This model is not declared multimodal, so tool results cannot carry images.");
        }

        return model.Protocol switch
        {
            LlmProtocols.AnthropicMessages => new LlmImageToolCapability(LlmImageToolDelivery.Native),
            LlmProtocols.OpenAiResponses => new LlmImageToolCapability(LlmImageToolDelivery.Native),
            LlmProtocols.OpenAiChat => new LlmImageToolCapability(LlmImageToolDelivery.FollowUpUserMessage),
            _ => new LlmImageToolCapability(
                LlmImageToolDelivery.Unsupported,
                $"Protocol '{model.Protocol}' is not supported for image tool results.")
        };
    }
}
