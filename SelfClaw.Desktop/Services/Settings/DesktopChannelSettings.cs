namespace SelfClaw.Desktop.Services;

public sealed record DesktopChannelSettings
{
    public IReadOnlyDictionary<string, DesktopChannelConfiguration> Items { get; init; }
        = new Dictionary<string, DesktopChannelConfiguration>(StringComparer.OrdinalIgnoreCase);

    public static DesktopChannelSettings Default { get; } = new();
}
