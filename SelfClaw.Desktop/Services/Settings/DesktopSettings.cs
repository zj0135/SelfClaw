using System.Text.Json.Serialization;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopSettings
{
    public const int DefaultModelContextWindow = 256_000;
    public const int DefaultModelAutoCompactTokenLimit = 200_000;

    [JsonPropertyName("theme_preference")]
    public AppThemePreference ThemePreference { get; init; } = AppThemePreference.System;

    [JsonPropertyName("model_context_window")]
    public int ModelContextWindow { get; init; } = DefaultModelContextWindow;

    [JsonPropertyName("model_auto_compact_token_limit")]
    public int ModelAutoCompactTokenLimit { get; init; } = DefaultModelAutoCompactTokenLimit;

    [JsonPropertyName("selected_workspace_root_id")]
    public Guid? SelectedWorkspaceRootId { get; init; }

    public DesktopChannelSettings Channels { get; init; } = DesktopChannelSettings.Default;

    [JsonPropertyName("mcp_servers")]
    public IReadOnlyDictionary<string, DesktopMcpServerConfiguration> McpServers { get; init; }
        = new Dictionary<string, DesktopMcpServerConfiguration>(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("disabled_skills")]
    public IReadOnlyList<string> DisabledSkills { get; init; } = [];

    public static DesktopSettings Default { get; } = new();
}
