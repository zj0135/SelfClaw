namespace SelfClaw.Core.Models;

/// <summary>
/// The shell the <c>run_shell_command</c> workspace tool uses to execute a command. The kind, not an
/// executable path, is what gets persisted: the runner re-resolves the executable at execution time so a
/// shell that was uninstalled fails with guidance instead of silently running something else.
/// </summary>
public enum WorkspaceShellKind
{
    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>), the default.</summary>
    WindowsPowerShell = 0,

    /// <summary>PowerShell 7+ (<c>pwsh.exe</c>).</summary>
    PowerShellSeven = 1,

    /// <summary>The Windows command interpreter (<c>cmd.exe</c>).</summary>
    CommandPrompt = 2,

    /// <summary>Git for Windows' Bash (<c>bash.exe</c>), resolved from the Git installation.</summary>
    GitBash = 3,
}
