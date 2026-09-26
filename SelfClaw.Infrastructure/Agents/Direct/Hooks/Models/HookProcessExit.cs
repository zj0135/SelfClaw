namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal enum HookProcessExit
{
    Exited,
    TimedOut,
    OutputTooLarge,
    LaunchFailed
}
