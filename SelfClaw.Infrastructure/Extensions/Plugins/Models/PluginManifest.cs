namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Version,
    string Description,
    string? Publisher,
    IReadOnlyList<string> Permissions,
    PluginContributions Contributions);
