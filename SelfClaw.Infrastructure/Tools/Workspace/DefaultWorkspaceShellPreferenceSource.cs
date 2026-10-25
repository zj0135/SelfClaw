using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

/// <summary>
/// Default preference source used until a host supplies its settings-backed one: the shell stays at
/// Windows PowerShell, so the tool behaves exactly as it did before the setting existed.
/// </summary>
internal sealed class DefaultWorkspaceShellPreferenceSource : IWorkspaceShellPreferenceSource
{
    private static readonly WorkspaceShellPreference DefaultPreference = new();

    internal static DefaultWorkspaceShellPreferenceSource Instance { get; } = new();

    public Task<WorkspaceShellPreference> GetPreferenceAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultPreference);
}
