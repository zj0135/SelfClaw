namespace SelfClaw.Desktop.Services.Plugins.Models;

internal sealed record PersistedPluginTabs(IReadOnlyList<string> Tabs, string? ActiveKey);
