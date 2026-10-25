namespace SelfClaw.Core.Models;

/// <summary>
/// Whether one selectable shell is currently installed, for the settings page. <see cref="ResolvedPath"/>
/// is the executable the runner would launch; it is empty when the shell is unavailable.
/// </summary>
public sealed record WorkspaceShellAvailability(
    WorkspaceShellKind Kind,
    bool Available,
    string ResolvedPath);
