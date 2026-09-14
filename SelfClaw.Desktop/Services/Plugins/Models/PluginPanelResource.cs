namespace SelfClaw.Desktop.Services.Plugins.Models;

internal sealed record PluginPanelResource(int StatusCode, string Reason, string Headers, byte[] Content);
