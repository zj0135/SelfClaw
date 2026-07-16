namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Identifies a concrete AI provider implementation. The kind selects which
/// <c>IAiProviderAdapter</c> handles client creation and option mapping.
/// </summary>
public enum AiProviderKind
{
    OpenAI = 0,
    OpenAICompatible = 1,
    DeepSeek = 2,
    Anthropic = 3,
    GoogleGemini = 4,
    Ollama = 5,
    AzureOpenAI = 6
}
