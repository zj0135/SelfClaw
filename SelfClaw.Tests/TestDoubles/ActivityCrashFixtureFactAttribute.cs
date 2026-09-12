namespace SelfClaw.Tests.TestDoubles;

internal sealed class ActivityCrashFixtureFactAttribute : FactAttribute
{
    public ActivityCrashFixtureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELFCLAW_ACTIVITY_CRASH_ROOT") is null)
            Skip = "Only launched by the isolated activity process recovery test.";
    }
}
