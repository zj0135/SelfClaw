namespace SelfClaw.Infrastructure.Extensions.Plugins;

/// <summary>
/// Every panel is served from its own <c>https://&lt;plugin-id&gt;.plugin.selfclaw.local</c> origin. That is
/// not cosmetic: the distinct origin is what gives each Plugin its own renderer process, its own storage
/// partition, and an <c>event.origin</c> the shell can treat as unforgeable identity. Because the origin
/// is derived from the Plugin id, a Plugin that contributes panels needs an id that is also a legal DNS
/// label — stricter than the package id rules.
/// </summary>
internal static class PluginPanelOrigin
{
    public const string HostSuffix = ".plugin.selfclaw.local";

    private const int MaximumLabelLength = 63;

    public static bool IsValidPluginLabel(string? pluginId)
        => !string.IsNullOrEmpty(pluginId) &&
           pluginId.Length <= MaximumLabelLength &&
           !pluginId.StartsWith('-') &&
           !pluginId.EndsWith('-') &&
           pluginId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public static string ForPlugin(string pluginId)
        => $"https://{pluginId}{HostSuffix}";
}
