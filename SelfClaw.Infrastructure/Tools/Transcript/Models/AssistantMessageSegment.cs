namespace SelfClaw.Infrastructure.Tools.Transcript.Models;

public sealed record AssistantMessageSegment(
    AssistantMessageSegmentKind Kind,
    string Markdown,
    bool IsPending = false,
    Guid? ToolExecutionId = null);
