namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record PluginPanelContribution(
    string Id,
    string Title,
    string Icon,
    string Entry,
    int DefaultWidth);
