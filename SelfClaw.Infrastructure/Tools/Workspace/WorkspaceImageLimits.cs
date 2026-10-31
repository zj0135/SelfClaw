namespace SelfClaw.Infrastructure.Tools.Workspace;

/// <summary>
/// Single source of truth for the image limits of <c>read_file</c>: the tool description, the error
/// messages and the per-turn budget all read it, so they cannot drift apart.
/// </summary>
internal static class WorkspaceImageLimits
{
    internal const int MaximumBytes = 5 * 1024 * 1024;
    internal const int MaximumImagesPerTurn = 6;
    internal const int MaximumBytesPerTurn = 12 * 1024 * 1024;
}
