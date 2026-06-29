namespace SelfClaw.Infrastructure.Tools.Transcript.Models;

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
