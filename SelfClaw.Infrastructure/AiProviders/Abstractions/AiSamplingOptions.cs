namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

/// <summary>
/// Common sampling parameters shared across providers. Each value carries an
/// explicit enabled flag so an unset parameter is distinguished from a stored
/// default and is left off the request entirely.
/// </summary>
public sealed record AiSamplingOptions(
    bool TemperatureEnabled,
    double Temperature,
    bool TopPEnabled,
    double TopP);
