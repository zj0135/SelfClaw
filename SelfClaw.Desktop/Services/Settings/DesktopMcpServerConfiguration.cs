using System.Text.Json.Serialization;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopMcpServerConfiguration
{
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public IReadOnlyList<string> Args { get; init; } = [];

    public IReadOnlyDictionary<string, string> Env { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static DesktopMcpServerConfiguration Default { get; } = new();
}
