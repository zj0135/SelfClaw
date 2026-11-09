namespace SelfClaw.Core.Models;

/// <summary>
/// Chat-ness is a capability of the wire format, not of the provider: an OpenAI connection serves chat
/// and image models side by side, so every "can this model run a turn?" gate has to ask the format.
/// </summary>
public static class AiProviderApiFormatExtensions
{
    public static bool IsChatFormat(this AiProviderApiFormat apiFormat) =>
        apiFormat is AiProviderApiFormat.OpenAIChatCompletions
            or AiProviderApiFormat.OpenAIResponses
            or AiProviderApiFormat.AnthropicMessages;
}
