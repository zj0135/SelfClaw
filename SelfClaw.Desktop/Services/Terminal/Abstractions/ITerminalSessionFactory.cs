namespace SelfClaw.Desktop.Services.Terminal.Abstractions;

public interface ITerminalSessionFactory
{
    ITerminalSession Create(string workingDirectory, int columns, int rows);
}
