using System.Text.Json.Serialization;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopChannelSettings
{
    public IReadOnlyDictionary<string, DesktopChannelConfiguration> Items { get; init; }
        = new Dictionary<string, DesktopChannelConfiguration>(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FeishuDesktopChannelSettings? Feishu { get; init; }

    public static DesktopChannelSettings Default { get; } = new();
}
