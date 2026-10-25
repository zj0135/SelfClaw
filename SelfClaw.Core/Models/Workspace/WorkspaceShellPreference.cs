namespace SelfClaw.Core.Models;

/// <summary>
/// The user's choice of shell for the <c>run_shell_command</c> workspace tool. Read per turn (tool
/// descriptions) and per execution (process launch), so a change applies to the next turn without restart.
/// </summary>
public sealed record WorkspaceShellPreference(WorkspaceShellKind ShellKind = WorkspaceShellKind.WindowsPowerShell);
