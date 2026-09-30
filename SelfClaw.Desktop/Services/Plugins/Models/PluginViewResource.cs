namespace SelfClaw.Desktop.Services.Plugins.Models;

internal sealed record PluginViewResource(int StatusCode, string Reason, string Headers, byte[] Content);
