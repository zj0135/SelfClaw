using System.IO;
using SelfClaw.Desktop.Services.Terminal.Abstractions;

namespace SelfClaw.Desktop.Services.Terminal;

internal sealed class ConPtyTerminalSessionFactory : ITerminalSessionFactory
{
    public ITerminalSession Create(string workingDirectory, int columns, int rows)
        => new ConPtyTerminalSession(
            ResolvePowerShellExecutable(),
            workingDirectory,
            columns,
            rows);

    private static string ResolvePowerShellExecutable()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
}
