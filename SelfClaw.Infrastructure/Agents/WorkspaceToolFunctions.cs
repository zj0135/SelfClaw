using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Infrastructure.Agents;

internal sealed class WorkspaceToolFunctions
{
    private readonly WorkspaceRoot _workspaceRoot;
    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly RuntimeToolObserver _observer;

    public WorkspaceToolFunctions(
        WorkspaceRoot workspaceRoot,
        IWorkspaceToolService workspaceToolService,
        RuntimeToolObserver observer)
    {
        _workspaceRoot = workspaceRoot;
        _workspaceToolService = workspaceToolService;
        _observer = observer;
    }

    public async Task<IReadOnlyList<WorkspaceFileEntry>> ListWorkspaceFilesAsync(
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        var record = _observer.Start(
            "list_workspace_files",
            JsonSerializer.Serialize(new { relativePath = relativePath ?? string.Empty }));

        try
        {
            var result = await _workspaceToolService.ListFilesAsync(_workspaceRoot.RootPath, relativePath, cancellationToken);
            _observer.Complete(record, WorkspaceToolSummaries.Summarize(result));
            return result;
        }
        catch (Exception exception)
        {
            _observer.Fail(record, exception.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<WorkspaceSearchHit>> SearchWorkspaceTextAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var record = _observer.Start(
            "search_workspace_text",
            JsonSerializer.Serialize(new { query }));

        try
        {
            var result = await _workspaceToolService.SearchTextAsync(_workspaceRoot.RootPath, query, cancellationToken);
            _observer.Complete(record, WorkspaceToolSummaries.Summarize(result));
            return result;
        }
        catch (Exception exception)
        {
            _observer.Fail(record, exception.Message);
            throw;
        }
    }

    public async Task<WorkspaceFileContent> ReadWorkspaceFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var record = _observer.Start(
            "read_workspace_file",
            JsonSerializer.Serialize(new { relativePath }));

        try
        {
            var result = await _workspaceToolService.ReadFileAsync(_workspaceRoot.RootPath, relativePath, cancellationToken);
            _observer.Complete(record, WorkspaceToolSummaries.Summarize(result));
            return result;
        }
        catch (Exception exception)
        {
            _observer.Fail(record, exception.Message);
            throw;
        }
    }
}