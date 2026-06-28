namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Coarse classification of a tool/command invocation, used by the transcript to
/// pick an icon and verb. Parsers map agent-specific tool names onto these.
/// </summary>
public enum ToolCallKind
{
    Other = 0,
    Read = 1,
    Edit = 2,
    Run = 3,
    Search = 4,
    List = 5
}
