namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record RawPluginManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Version,
    string? Description,
    string? Publisher,
    IReadOnlyList<string?>? Permissions,
    RawPluginContributions? Contributes);
