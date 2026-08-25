using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Workspace.Abstractions;

public interface IWorkspaceSelectionController
{
    WorkspaceRoot? SelectedWorkspaceRoot { get; }

    IReadOnlyList<WorkspaceRoot> WorkspaceRoots { get; }

    Task ReloadWorkspaceSelectionAsync();

    void SelectWorkspaceRoot(Guid? workspaceRootId);

    Task<WorkspaceRoot> SelectOrAddWorkspaceRootAsync(string rootPath);

    Task DeleteWorkspaceRootAsync(Guid workspaceRootId);
}
