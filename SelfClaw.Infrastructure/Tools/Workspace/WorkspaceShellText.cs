using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

/// <summary>
/// User-facing copy derived from the active shell kind, shared by the tool description, the process
/// timeout subject and the result summaries so they always name the same shell.
/// </summary>
internal static class WorkspaceShellText
{
    // Says out loud what the tool cannot do: a dev server started with `&`, nohup or start /b would only
    // block the call until its timeout and then die with it, and the user gets no handle on it either way.
    private const string BackgroundNote =
        " The call waits for the command to finish, so it cannot keep a service running in the background.";

    internal static string Name(WorkspaceShellKind kind) => kind switch
    {
        WorkspaceShellKind.WindowsPowerShell => "PowerShell",
        WorkspaceShellKind.PowerShellSeven => "pwsh",
        WorkspaceShellKind.CommandPrompt => "cmd",
        WorkspaceShellKind.GitBash => "bash",
        _ => "shell",
    };

    internal static string ToolDescription(WorkspaceShellKind kind) => Describe(kind) + BackgroundNote;

    private static string Describe(WorkspaceShellKind kind) => kind switch
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
