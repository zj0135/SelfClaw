namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Selects which OpenAI wire format a model profile targets. A single provider
/// adapter may support more than one format.
/// </summary>
/// <remarks>
/// The numeric values are persisted in SQLite (see <c>SqliteMappings</c>) and are
/// therefore stable: retired formats leave a gap rather than renumbering the rest.
/// </remarks>
public enum AiProviderApiFormat
{
    OpenAIChatCompletions = 0,
    OpenAIResponses = 1,
    AnthropicMessages = 2,
    OllamaNative = 4
}
