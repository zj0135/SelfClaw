using Microsoft.Win32;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Processes;

/// <summary>
/// Resolves the executable behind each selectable workspace shell. Both the settings page (availability)
/// and <see cref="Tools.Workspace.WorkspaceShellRunner"/> (process launch) resolve through this one class,
/// so what the page reports is what the runner starts. Probing is read-only and cheap; results are never
/// cached because an install or uninstall between turns must be picked up without restart.
/// </summary>
public static class ShellExecutableLocator
{
    /// <summary>
    /// <c>System32\bash.exe</c> is the WSL bridge, not Git Bash: launching it would hand the command to a
    /// different filesystem view of the workspace, so PATH probing skips that directory entirely.
    /// </summary>
    private static readonly string System32Directory = Environment.GetFolderPath(Environment.SpecialFolder.System);

    public static string? TryFindWindowsPowerShell()
    {
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return FindOnPath("powershell.exe") ?? (File.Exists(fallback) ? fallback : null);
    }

    public static string? TryFindPowerShellSeven()
    {
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");
        return (File.Exists(fallback) ? fallback : null) ?? FindOnPath("pwsh.exe");
    }

    public static string? TryFindCommandPrompt()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && File.Exists(comSpec))
        {
            return comSpec;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    public static string? TryFindGitBash()
    {
        return TryFindGitBashFromRegistry()
            ?? TryFindGitBashFromCommonLocations()
            ?? FindOnPath("bash.exe", skipSystem32: true);
    }

    /// <param name="pathEnvironment">Overridable so tests can drive PATH probing with a fake value.</param>
    /// <param name="skipSystem32">Set for Git Bash; see <see cref="System32Directory"/>.</param>
    internal static string? FindOnPath(string fileName, string? pathEnvironment = null, bool skipSystem32 = false)
    {
        var directories = (pathEnvironment ?? Environment.GetEnvironmentVariable("PATH"))?.Split(';');
        if (directories is null)
        {
            return null;
        }

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory.Trim(), fileName));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (skipSystem32 && string.Equals(
                    Path.GetDirectoryName(candidate), System32Directory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryFindGitBashFromRegistry()
    {
        // Git for Windows writes its install root (both per-machine and per-user installs) here.
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\GitForWindows");
                if (key?.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath))
                {
                    var bashPath = Path.Combine(installPath, "bin", "bash.exe");
                    if (File.Exists(bashPath))
                    {
                        return bashPath;
                    }
                }
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or IOException)
            {
                // Registry access is best-effort; the common locations and PATH follow.
            }
        }

        return null;
    }

    private static string? TryFindGitBashFromCommonLocations()
    {
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git"),
        ];

        return roots
            .Select(root => Path.Combine(root, "bin", "bash.exe"))
            .FirstOrDefault(File.Exists);
    }
}
