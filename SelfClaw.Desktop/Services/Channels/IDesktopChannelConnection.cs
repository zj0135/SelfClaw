namespace SelfClaw.Desktop.Services;

public interface IDesktopChannelConnection : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task ReplyAsync(
        DesktopChannelIncomingMessage incomingMessage,
        string content,
        CancellationToken cancellationToken = default);

    Task<IDesktopChannelStreamingReply?> CreateStreamingReplyAsync(
        DesktopChannelIncomingMessage incomingMessage,
        CancellationToken cancellationToken = default);
}

public interface IDesktopChannelStreamingReply
{
    Task UpdateAsync(string content, CancellationToken cancellationToken = default);

    Task FinishAsync(string content, CancellationToken cancellationToken = default);
}
