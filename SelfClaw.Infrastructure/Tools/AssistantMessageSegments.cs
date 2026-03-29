namespace SelfClaw.Infrastructure.Tools;

public sealed record AssistantMessageSegments(
    string ContentMarkdown,
    string? ThinkingMarkdown)
{
    public bool HasThinking => !string.IsNullOrWhiteSpace(ThinkingMarkdown);
}
