namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record RawPluginPanelContribution(
    string? Id = null,
    string? Title = null,
    string? Icon = null,
    string? Entry = null,
    int? DefaultWidth = null);
