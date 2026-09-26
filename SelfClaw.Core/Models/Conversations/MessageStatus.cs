namespace SelfClaw.Core.Models;

public enum MessageStatus
{
    Completed = 0,
    Streaming = 1,
    Failed = 2,
    Cancelled = 3,

    /// <summary>
    /// The assistant answer stopped at the output-token cap. Unlike <see cref="Failed"/>,
    /// the partial content is valid and stays in the prompt history so the model can
    /// resume from it when the user chooses to continue.
    /// </summary>
    Truncated = 4,

    /// <summary>
    /// A plugin hook blocked the turn before any model call. Interactive turns only; delegated
    /// turns rewrite it to <see cref="Failed"/> so durable task state stays consistent.
    /// </summary>
    Blocked = 5
}
