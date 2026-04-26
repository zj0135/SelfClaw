namespace SelfClaw.Infrastructure.Channels.Feishu;

/// <summary>
/// Configuration for the C# Feishu channel runtime.
/// </summary>
public sealed class FeishuChannelOptions
{
    public required string AppId { get; init; }
    public required string AppSecret { get; init; }
    public string BaseUrl { get; init; } = FeishuApiClient.DefaultBaseUrl;
    public string? BotDisplayName { get; init; }
    public HttpClient? HttpClient { get; init; }
    public Action<string>? Log { get; init; }
}
