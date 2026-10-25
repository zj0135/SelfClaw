using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Supplies the shell preference for the <c>run_shell_command</c> workspace tool. Infrastructure
/// registers a default that always answers Windows PowerShell; a host that owns the settings file
/// replaces it with a settings-backed implementation.
/// </summary>
public interface IWorkspaceShellPreferenceSource
{
    Task<WorkspaceShellPreference> GetPreferenceAsync(CancellationToken cancellationToken = default);
}
