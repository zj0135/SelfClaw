namespace SelfClaw.Tests.TestDoubles;

internal sealed class DesktopSmokeFactAttribute : FactAttribute
{
    public DesktopSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELFCLAW_DESKTOP_SMOKE") != "1")
            Skip = "Set SELFCLAW_DESKTOP_SMOKE=1 to run the interactive WPF/WebView2 smoke test.";
    }
}
