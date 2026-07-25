using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IWorkspaceToolService
{
    Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
        string workspaceRootPath,
        string? relativePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(
        string workspaceRootPath,
        string pattern,
        string? relativePath = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
        string workspaceRootPath,
        string query,
        WorkspaceSearchOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<WorkspaceFileContent> ReadFileAsync(
        string workspaceRootPath,
        string relativePath,
        int? startLine = null,
        int? lineCount = null,
        CancellationToken cancellationToken = default);

    Task<WorkspaceFileWriteResult> WriteFileAsync(
        string workspaceRootPath,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default);

    Task<WorkspaceFileWriteResult> EditFileAsync(
        string workspaceRootPath,
        string relativePath,
        string oldText,
        string newText,
        bool replaceAll = false,
        CancellationToken cancellationToken = default);

    Task<ShellCommandResult> RunShellCommandAsync(
        string workspaceRootPath,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);
}
