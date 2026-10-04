namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// Host-controlled gates for the durable Queue and for Steer. Both are intentionally disabled by
/// default: an idle Direct send still flows through the single accept → dispatcher → admission
/// path, but a busy conversation cannot accept a follow-up, and a running turn never consumes a
/// steer until the acceptance environment enables the corresponding switch.
/// </summary>
internal sealed class ConversationInputFeatureSwitch
{
    internal const string EnvironmentVariable = "SELFCLAW_QUEUE_ENABLED";
    internal const string SteerEnvironmentVariable = "SELFCLAW_STEER_ENABLED";

    public ConversationInputFeatureSwitch()
        : this(Environment.GetEnvironmentVariable(EnvironmentVariable),
            Environment.GetEnvironmentVariable(SteerEnvironmentVariable))
    {
    }

    internal ConversationInputFeatureSwitch(string? value, string? steerValue = null)
    {
        QueueEnabled = Parse(value);
        SteerEnabled = Parse(steerValue);
    }

    public bool QueueEnabled { get; }

    public bool SteerEnabled { get; }

    internal static bool Parse(string? value)
        => value is not null && (value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
