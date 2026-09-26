namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record RawPluginHookMatcher(
    IReadOnlyList<string?>? Origins = null,
    IReadOnlyList<string?>? Tools = null,
    IReadOnlyList<string?>? SourceIds = null,
    IReadOnlyList<string?>? Kinds = null,
    IReadOnlyList<string?>? Sources = null,
    IReadOnlyList<string?>? Hosts = null);
