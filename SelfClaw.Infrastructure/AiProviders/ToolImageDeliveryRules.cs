using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// The single gate for whether a model can receive images inside tool results, and how they reach it.
/// The model's own multimodal declaration is the only explicit signal; the wire format then decides the
/// shape. Unverified formats stay unsupported, because a wrong guess ships base64 to a provider that
/// treats it as a giant JSON string.
/// </summary>
internal static class ToolImageDeliveryRules
{
    internal static ToolImageCapability Resolve(AiModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Configuration?.IsMultimodal != true)
        {
            return new ToolImageCapability(
                ToolImageDelivery.Unsupported,
                "This model is not marked as multimodal. Enable multimodal support for it in AI provider settings.");
        }

        return profile.ApiFormat switch
        {
            AiProviderApiFormat.AnthropicMessages => new ToolImageCapability(ToolImageDelivery.Native, null),
            AiProviderApiFormat.OpenAIResponses => new ToolImageCapability(ToolImageDelivery.Native, null),
            AiProviderApiFormat.OpenAIChatCompletions => new ToolImageCapability(ToolImageDelivery.FollowUpUserMessage, null),
            _ => new ToolImageCapability(
                ToolImageDelivery.Unsupported,
                $"API format '{profile.ApiFormat}' is not supported for image tool results.")
        };
    }
}
