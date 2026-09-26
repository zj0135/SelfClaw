namespace SelfClaw.Core.Models;

public enum MessageSegmentKind
{
    Text = 0,
    Thinking = 1,
    ToolCall = 2,

    /// <summary>
    /// A turn-level diagnostic notice produced by the host (for example when a Plugin hook modified
    /// the turn). It enters the conversation record only and is never replayed to the model.
    /// </summary>
    Notice = 3
}
