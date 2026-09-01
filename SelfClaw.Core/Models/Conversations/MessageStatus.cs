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
    Truncated = 4
}
