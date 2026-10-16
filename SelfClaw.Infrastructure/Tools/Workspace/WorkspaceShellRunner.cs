using System.Diagnostics;
using System.Text;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Processes;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal sealed class WorkspaceShellRunner
{
    internal const int MaxShellOutputCharacters = ProcessRunner.MaxOutputCharacters;
    private const string TimeoutSubject = "PowerShell command";
    private const string SkillsInstallMarker = "SelfClaw/skills";
    private const string PluginsInstallMarker = "SelfClaw/plugins";

    /// <summary>
    /// An audit heuristic, not a security boundary: PowerShell cannot be confined in-process, so a shell
    /// command that reaches for a Skill or Plugin installation directory is the one signal we can surface.
    /// It matches on the segment shape rather than the resolved path, so a renamed application-data root
    /// would need this updated.
    /// </summary>
    internal static bool TargetsSkillOrPluginInstall(string command)
    {
        var normalized = command.Replace('\\', '/');
        return normalized.Contains(SkillsInstallMarker, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(PluginsInstallMarker, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ShellCommandResult> RunShellCommandAsync(
        string workspaceRootPath,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A shell command is required.", nameof(command));
        }

        var root = WorkspaceFileAccess.NormalizeRoot(workspaceRootPath);
        var result = await ProcessRunner.RunAsync(
                CreateProcessStartInfo(root, command),
                timeoutSeconds,
                TimeoutSubject,
                cancellationToken)
            .ConfigureAwait(false);
        return CreateResult(command, result);
    }

    private static ProcessStartInfo CreateProcessStartInfo(string root, string command)
    {
        // Encoding is set on the piped streams by ProcessRunner; these assignments make PowerShell
        // itself emit UTF-8 rather than the console code page.
        var script = string.Join(
            Environment.NewLine,
            "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)",
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
            "$OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
            command);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            WorkingDirectory = root
        };
    }

    private static ShellCommandResult CreateResult(string command, ProcessRunResult result)
        => new(
            command,
            true,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.OutputTruncated,
            result.ExitCode == 0
                ? "PowerShell command completed."
                : $"PowerShell exited with code {result.ExitCode}.");
}
