namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record RawPluginViewContribution(
    string? Id = null,
    string? Title = null,
    string? Icon = null,
    string? Slot = null,
    string? Entry = null,
    int? DefaultWidth = null);
