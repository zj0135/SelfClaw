using System.Text.Json;

namespace SelfClaw.Infrastructure.Channels.Feishu;

internal sealed class FeishuWsClientConfig
{
    public int ReconnectCount { get; set; } = -1;
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromMinutes(2);
    public int ReconnectNonceSeconds { get; set; } = 30;
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromMinutes(2);

    public static FeishuWsClientConfig Parse(JsonElement element)
    {
        return new FeishuWsClientConfig
        {
            ReconnectCount = FeishuJson.GetInt32(element, "ReconnectCount") ?? -1,
            ReconnectInterval = TimeSpan.FromSeconds(
                Math.Max(1, FeishuJson.GetInt32(element, "ReconnectInterval") ?? 120)),
            ReconnectNonceSeconds = Math.Max(0, FeishuJson.GetInt32(element, "ReconnectNonce") ?? 30),
            PingInterval = TimeSpan.FromSeconds(
                Math.Max(1, FeishuJson.GetInt32(element, "PingInterval") ?? 120))
        };
    }
}
