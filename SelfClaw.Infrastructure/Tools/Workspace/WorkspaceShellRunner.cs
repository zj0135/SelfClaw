using System.Diagnostics;
using System.Text;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Processes;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal sealed class WorkspaceShellRunner
{
    internal const int MaxShellOutputCharacters = ProcessRunner.MaxOutputCharacters;
    private const string SkillsInstallMarker = "SelfClaw/skills";
    private const string PluginsInstallMarker = "SelfClaw/plugins";
    private const string GitBashMissingMessage =
        "Git Bash was selected as the workspace shell, but no Git for Windows installation was found. " +
        "Install Git for Windows or pick another shell in Settings.";

    private readonly IWorkspaceShellPreferenceSource? _preferenceSource;

    public WorkspaceShellRunner(IWorkspaceShellPreferenceSource? preferenceSource = null)
    {
        _preferenceSource = preferenceSource;
    }

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
        var preference = _preferenceSource is null
            ? new WorkspaceShellPreference()
            : await _preferenceSource.GetPreferenceAsync(cancellationToken).ConfigureAwait(false);
        var result = await ProcessRunner.RunAsync(
                CreateProcessStartInfo(root, command, preference.ShellKind),
                timeoutSeconds,
                $"{WorkspaceShellText.Name(preference.ShellKind)} command",
                cancellationToken)
            .ConfigureAwait(false);
        return CreateResult(command, preference.ShellKind, result);
    }

    internal static ProcessStartInfo CreateProcessStartInfo(string root, string command, WorkspaceShellKind kind)
    {
        var startInfo = new ProcessStartInfo { WorkingDirectory = root };
        switch (kind)
        {
            case WorkspaceShellKind.WindowsPowerShell or WorkspaceShellKind.PowerShellSeven:
                startInfo.FileName = kind == WorkspaceShellKind.WindowsPowerShell
                    ? "powershell.exe"
                    : "pwsh.exe";
                foreach (var argument in new[]
                {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                    EncodePowerShellScript(command)
                })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                break;
            case WorkspaceShellKind.CommandPrompt:
                startInfo.FileName = "cmd.exe";
                // cmd's built-ins write the pipes in the system OEM code page and an inline `chcp 65001`
                // cannot change that (it only switches the console, and the command text itself has
                // already been transcoded at parse time), so the pipes are decoded as OEM instead.
                var oemCodePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                startInfo.StandardOutputEncoding = oemCodePage > 0 ? Encoding.GetEncoding(oemCodePage) : Encoding.Default;
                startInfo.StandardErrorEncoding = startInfo.StandardOutputEncoding;
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/s");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
                break;
            case WorkspaceShellKind.GitBash:
                startInfo.FileName = ShellExecutableLocator.TryFindGitBash()
                    ?? throw new InvalidOperationException(GitBashMissingMessage);
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(command);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown workspace shell kind.");
        }

        return startInfo;
    }

    /// <summary>
    /// PowerShell receives the command as a UTF-16LE base64 script so no character survives a command-line
    /// round-trip. The preamble makes PowerShell itself emit UTF-8 rather than the console code page;
    /// the pipe encodings are set by ProcessRunner.
    /// </summary>
    private static string EncodePowerShellScript(string command)
    {
        var script = string.Join(
            Environment.NewLine,
            "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)",
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
            "$OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
            command);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static ShellCommandResult CreateResult(string command, WorkspaceShellKind kind, ProcessRunResult result)
    {
        var name = WorkspaceShellText.Name(kind);
        return new(
            command,
            kind,
            true,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.OutputTruncated,
            result.ExitCode == 0
                ? $"{name} command completed."
                : $"{name} exited with code {result.ExitCode}.");
    }
}
