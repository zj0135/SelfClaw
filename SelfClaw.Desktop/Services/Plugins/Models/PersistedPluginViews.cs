namespace SelfClaw.Desktop.Services.Plugins.Models;

internal sealed record PersistedPluginViews(IReadOnlyList<string> Views, string? ActiveView);
