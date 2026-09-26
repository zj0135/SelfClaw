namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record RawPluginHookContribution(
    string? Id = null,
    string? Event = null,
    RawPluginHookMatcher? Matcher = null,
    string? Command = null,
    IReadOnlyList<string?>? Arguments = null,
    int? TimeoutSeconds = null,
    string? OnFailure = null,
    bool? Async = null,
    bool? IncludeRequestBody = null);
