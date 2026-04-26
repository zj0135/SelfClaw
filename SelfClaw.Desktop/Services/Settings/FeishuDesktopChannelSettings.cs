namespace SelfClaw.Desktop.Services;

public sealed record FeishuDesktopChannelSettings
{
    public bool Enabled { get; init; }

    public string DisplayName { get; init; } = "\u93B4\u6220\u6B91\u690B\u70B0\u529F";

    public string AppId { get; init; } = string.Empty;

    public string SecretRef { get; init; } = string.Empty;

    public string BotDisplayName { get; init; } = string.Empty;

    public Guid? ProfileId { get; init; }

    public static FeishuDesktopChannelSettings Default { get; } = new();
}
