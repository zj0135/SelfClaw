namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// Host-controlled gate for the durable Queue. It is intentionally disabled by default: an idle
/// Direct send still flows through the single accept → dispatcher → admission path, but a busy
/// conversation cannot accept a follow-up and the dispatcher never auto-chains a backlog until the
/// acceptance environment enables the switch.
/// </summary>
internal sealed class ConversationInputFeatureSwitch
{
    internal const string EnvironmentVariable = "SELFCLAW_QUEUE_ENABLED";

    public ConversationInputFeatureSwitch()
        : this(Environment.GetEnvironmentVariable(EnvironmentVariable))
    {
    }

    internal ConversationInputFeatureSwitch(string? value)
    {
        QueueEnabled = Parse(value);
    }

    public bool QueueEnabled { get; }

    internal static bool Parse(string? value)
        => value is not null && (value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
