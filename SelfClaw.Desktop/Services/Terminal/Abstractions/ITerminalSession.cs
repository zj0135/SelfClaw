namespace SelfClaw.Desktop.Services.Terminal.Abstractions;

public interface ITerminalSession : IDisposable
{
    event EventHandler<string>? OutputReceived;

    event EventHandler<int?>? Exited;

    void Start();

    void WriteInput(string input);

    void Resize(int columns, int rows);
}
