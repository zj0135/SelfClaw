namespace SelfClaw.Infrastructure.Tools.Transcript;

public enum AssistantMessageSegmentKind
{
    Content,
    Thinking,
    ToolAnchor
}

public sealed record AssistantMessageSegment(
    AssistantMessageSegmentKind Kind,
    string Markdown,
    bool IsPending = false,
    Guid? ToolExecutionId = null);

public sealed record AssistantMessageSegments(
    string ContentMarkdown,
    IReadOnlyList<AssistantMessageSegment> Segments)
{
    public bool HasThinking => Segments.Any(item => item.Kind == AssistantMessageSegmentKind.Thinking);

    public string? ThinkingMarkdown
    {
        get
        {
            var thinkingSegments = Segments
                .Where(item => item.Kind == AssistantMessageSegmentKind.Thinking)
                .Select(item => item.Markdown)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            return thinkingSegments.Length == 0
                ? null
                : string.Join("\n\n", thinkingSegments);
        }
    }
}
