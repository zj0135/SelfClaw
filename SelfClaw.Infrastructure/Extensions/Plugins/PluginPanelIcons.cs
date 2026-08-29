namespace SelfClaw.Infrastructure.Extensions.Plugins;

/// <summary>
/// Tab icons are names from a fixed set, never package content. The tab bar renders inside the
/// application origin, so accepting a package-supplied SVG would hand every Plugin an injection surface
/// into the shell for the sake of a 14px glyph.
/// </summary>
internal static class PluginPanelIcons
{
    public const string Default = "puzzle";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "activity", "book-open", "bookmark", "bug", "calendar", "clipboard", "code", "database",
        "eye", "file-code", "file-text", "filter", "folder", "folder-open", "git-branch", "globe",
        "image", "info", "key", "layers", "layout-grid", "lightbulb", "link", "list", "map",
        "message-square", "package", "play", "puzzle", "search", "settings", "shield", "sparkles",
        "star", "table", "tag", "terminal", "timer", "wrench", "zap"
    };

    public static string Resolve(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return Default;
        }

        var normalized = icon.Trim().ToLowerInvariant();
        return Allowed.Contains(normalized)
            ? normalized
            : throw new InvalidDataException(
                $"Plugin panel icon '{icon}' is not a supported icon name.");
    }
}
