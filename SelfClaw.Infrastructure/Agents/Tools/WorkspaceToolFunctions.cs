using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Tools;

internal sealed class WorkspaceToolFunctions
{
    private readonly WorkspaceRoot _workspaceRoot;
    private readonly IWorkspaceToolService _workspaceToolService;

    public WorkspaceToolFunctions(
        WorkspaceRoot workspaceRoot,
        IWorkspaceToolService workspaceToolService)
    {
        _workspaceRoot = workspaceRoot;
        _workspaceToolService = workspaceToolService;
    }

    public Task<IReadOnlyList<WorkspaceFileEntry>> ListWorkspaceFilesAsync(
        string? relativePath = null,
        CancellationToken cancellationToken = default)
        => _workspaceToolService.ListFilesAsync(_workspaceRoot.RootPath, relativePath, cancellationToken);

    public Task<IReadOnlyList<WorkspaceSearchHit>> SearchWorkspaceTextAsync(
        string query,
        CancellationToken cancellationToken = default)
        => _workspaceToolService.SearchTextAsync(_workspaceRoot.RootPath, query, cancellationToken);

    public Task<WorkspaceFileContent> ReadWorkspaceFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
        => _workspaceToolService.ReadFileAsync(_workspaceRoot.RootPath, relativePath, cancellationToken);

    public async Task<WorkspaceFileWriteResult> WriteWorkspaceFileAsync(
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        content ??= string.Empty;
        return await _workspaceToolService.WriteFileAsync(_workspaceRoot.RootPath, relativePath, content, cancellationToken);
    }

    public async Task<ShellCommandResult> RunShellCommandAsync(
        string command,
        int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
        => await _workspaceToolService.RunShellCommandAsync(_workspaceRoot.RootPath, command, timeoutSeconds, cancellationToken);
}
