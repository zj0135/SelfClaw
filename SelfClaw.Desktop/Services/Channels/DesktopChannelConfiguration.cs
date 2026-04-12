namespace SelfClaw.Desktop.Services;

public sealed record DesktopChannelConfiguration
{
    public bool Enabled { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public Guid? ProfileId { get; init; }

    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> SecretRefs { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static DesktopChannelConfiguration Default { get; } = new();
}
