using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IWorkspaceRootRepository
{
    Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default);

    Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default);
}
