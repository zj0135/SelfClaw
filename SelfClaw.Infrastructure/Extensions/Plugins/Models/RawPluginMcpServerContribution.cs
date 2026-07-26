namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record RawPluginMcpServerContribution(
    string Id,
    string Name,
    string Transport,
    string? Command,
    IReadOnlyList<string?>? Arguments,
    string? Endpoint,
    string? TransportMode,
    int? ConnectionTimeoutSeconds,
    bool RequiresWorkspace,
    IReadOnlyList<RawPluginRequiredSetting>? RequiredSettings);
