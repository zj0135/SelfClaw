using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IWorkspaceToolService
{
    Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
        string workspaceRootPath,
        string? relativePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
        string workspaceRootPath,
        string query,
        CancellationToken cancellationToken = default);

    Task<WorkspaceFileContent> ReadFileAsync(
        string workspaceRootPath,
        string relativePath,
        CancellationToken cancellationToken = default);
}