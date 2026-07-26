namespace SelfClaw.Core.Models;

public enum McpServerHealthStatus
{
    Unknown = 0,
    Ready = 1,
    Connecting = 2,
    Degraded = 3,
    Disabled = 4,
    NeedsConfiguration = 5,
    Broken = 6
}
