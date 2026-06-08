namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

/// <summary>
/// Selects which OpenAI wire format a model profile targets. A single provider
/// adapter may support more than one format.
/// </summary>
public enum AiProviderApiFormat
{
    OpenAIChatCompletions = 0,
    OpenAIResponses = 1
}
