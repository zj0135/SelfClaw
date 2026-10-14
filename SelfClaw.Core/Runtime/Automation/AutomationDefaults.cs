namespace SelfClaw.Core.Runtime;

public static class AutomationDefaults
{
    public static readonly TimeSpan MissedRunGrace = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxRunSeconds = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DeleteWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ShutdownCancelWait = TimeSpan.FromSeconds(5);
    public const int ProjectionLimit = 300;
    public const int MaximumTitleLength = 120;
}
