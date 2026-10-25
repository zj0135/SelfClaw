using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

/// <summary>
/// User-facing copy derived from the active shell kind, shared by the tool description, the process
/// timeout subject and the result summaries so they always name the same shell.
/// </summary>
internal static class WorkspaceShellText
{
    internal static string Name(WorkspaceShellKind kind) => kind switch
    {
        WorkspaceShellKind.WindowsPowerShell => "PowerShell",
        WorkspaceShellKind.PowerShellSeven => "pwsh",
        WorkspaceShellKind.CommandPrompt => "cmd",
        WorkspaceShellKind.GitBash => "bash",
        _ => "shell",
    };

    internal static string ToolDescription(WorkspaceShellKind kind) => kind switch
    {
        WorkspaceShellKind.WindowsPowerShell =>
            "Run a Windows PowerShell command with the current workspace as its working directory.",
        WorkspaceShellKind.PowerShellSeven =>
            "Run a PowerShell 7 (pwsh) command with the current workspace as its working directory.",
        WorkspaceShellKind.CommandPrompt =>
            "Run a cmd.exe command (batch syntax, %VAR% variables) with the current workspace as its working directory.",
        WorkspaceShellKind.GitBash =>
            "Run a Git Bash (bash, POSIX syntax) command with the current workspace as its working directory.",
        _ => "Run a shell command with the current workspace as its working directory.",
    };
}
