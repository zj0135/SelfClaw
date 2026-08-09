namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Identifies a concrete AI provider implementation. The kind selects which
/// <c>IAiProviderAdapter</c> handles client creation and option mapping.
/// </summary>
/// <remarks>
/// The numeric values are persisted in SQLite (see <c>SqliteMappings</c>) and are
/// therefore stable: retired kinds leave a gap rather than renumbering the rest.
/// </remarks>
public enum AiProviderKind
{
    OpenAI = 0,
    OpenAICompatible = 1,
    Anthropic = 3,
    Ollama = 5
}
