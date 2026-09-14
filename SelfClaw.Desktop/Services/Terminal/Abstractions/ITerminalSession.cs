namespace SelfClaw.Desktop.Services.Terminal.Abstractions;

public interface ITerminalSession : IDisposable, IAsyncDisposable
{
    event EventHandler<string>? OutputReceived;

    event EventHandler<int?>? Exited;

    void Start();

    Task WriteInputAsync(string input, CancellationToken cancellationToken = default);

    void Resize(int columns, int rows);
}
