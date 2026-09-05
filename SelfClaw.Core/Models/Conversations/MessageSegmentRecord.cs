namespace SelfClaw.Core.Models;

/// <summary>
/// One ordered content block of an assistant message. <paramref name="Text"/> carries
/// Text/Thinking blocks; <paramref name="ToolRunId"/> points at the matching tool run for
/// ToolCall blocks. Rendering and model replay both walk the block list; the block order
/// is the transcript order.
/// </summary>
public sealed record MessageSegmentRecord(
    Guid MessageId,
    int Ordinal,
    MessageSegmentKind Kind,
    string? Text,
    Guid? ToolRunId);
