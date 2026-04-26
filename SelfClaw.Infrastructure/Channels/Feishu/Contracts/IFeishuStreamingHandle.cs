namespace SelfClaw.Infrastructure.Channels.Feishu;

public interface IFeishuStreamingHandle
{
    Task UpdateAsync(string content, CancellationToken cancellationToken = default);
    Task FinishAsync(string finalContent, CancellationToken cancellationToken = default);
}
