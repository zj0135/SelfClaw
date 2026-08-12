using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IGitWorkspaceQuery
{
    Task<GitWorkspaceState> GetStateAsync(
        WorkspaceRoot workspaceRoot,
        CancellationToken cancellationToken = default);
}
