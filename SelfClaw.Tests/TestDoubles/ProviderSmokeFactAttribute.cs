namespace SelfClaw.Tests.TestDoubles;

internal sealed class ProviderSmokeFactAttribute : FactAttribute
{
    public ProviderSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELFCLAW_PROVIDER_SMOKE") != "1")
            Skip = "Set SELFCLAW_PROVIDER_SMOKE=1 to call a configured provider with a synthetic prompt.";
    }
}
