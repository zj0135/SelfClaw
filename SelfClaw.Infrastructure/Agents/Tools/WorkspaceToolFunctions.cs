using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Infrastructure.Agents.Tools;

internal sealed class WorkspaceToolFunctions
{
    private readonly WorkspaceRoot _workspaceRoot;
    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly RuntimeToolObserver _observer;
    private readonly ToolPermissionMode _toolPermissionMode;
    private readonly IToolApprovalHandler? _toolApprovalHandler;

    public WorkspaceToolFunctions(
        WorkspaceRoot workspaceRoot,
        IWorkspaceToolService workspaceToolService,
        RuntimeToolObserver observer,
        ToolPermissionMode toolPermissionMode,
        IToolApprovalHandler? toolApprovalHandler)
    {
        _workspaceRoot = workspaceRoot;
        _workspaceToolService = workspaceToolService;
        _observer = observer;
        _toolPermissionMode = toolPermissionMode;
        _toolApprovalHandler = toolApprovalHandler;
    }

    public Task<IReadOnlyList<WorkspaceFileEntry>> ListWorkspaceFilesAsync(
        string? relativePath = null,
        CancellationToken cancellationToken = default)
        => ExecuteObservedAsync(
            "list_workspace_files",
            JsonSerializer.Serialize(new { relativePath = relativePath ?? string.Empty }),
            () => _workspaceToolService.ListFilesAsync(_workspaceRoot.RootPath, relativePath, cancellationToken),
            WorkspaceToolSummaries.Summarize,
            WorkspaceToolSummaries.Describe);

    public Task<IReadOnlyList<WorkspaceSearchHit>> SearchWorkspaceTextAsync(
        string query,
        CancellationToken cancellationToken = default)
        => ExecuteObservedAsync(
            "search_workspace_text",
            JsonSerializer.Serialize(new { query }),
            () => _workspaceToolService.SearchTextAsync(_workspaceRoot.RootPath, query, cancellationToken),
            WorkspaceToolSummaries.Summarize,
            WorkspaceToolSummaries.Describe);

    public Task<WorkspaceFileContent> ReadWorkspaceFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
        => ExecuteObservedAsync(
            "read_workspace_file",
            JsonSerializer.Serialize(new { relativePath }),
            () => _workspaceToolService.ReadFileAsync(_workspaceRoot.RootPath, relativePath, cancellationToken),
            WorkspaceToolSummaries.Summarize,
            WorkspaceToolSummaries.Describe);

    public async Task<WorkspaceFileWriteResult> WriteWorkspaceFileAsync(
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        content ??= string.Empty;
        var argumentsJson = JsonSerializer.Serialize(new
        {
            relativePath,
            characterCount = content.Length
        });
        var record = _observer.Start("write_workspace_file", argumentsJson);

        try
        {
            if (RequiresApproval)
            {
                record = _observer.AwaitApproval(record, "Waiting for your confirmation in the activity panel.");
                if (!await RequestApprovalAsync(
                        new ToolApprovalRequest(
                            record.Id,
                            "write_workspace_file",
                            "Write Workspace File",
                            $"Allow the agent to create or overwrite '{relativePath}' inside the selected workspace?\n\nCharacters: {content.Length}",
                            argumentsJson),
                        cancellationToken))
                {
                    var denied = new WorkspaceFileWriteResult(
                        relativePath,
                        false,
                        false,
                        content.Length,
                        "User denied approval.");
                    _observer.Cancel(record, WorkspaceToolSummaries.Summarize(denied));
                    return denied;
                }

                record = _observer.Resume(record, "Approval granted. Writing file...");
            }

            var result = await _workspaceToolService.WriteFileAsync(_workspaceRoot.RootPath, relativePath, content, cancellationToken);
            _observer.Complete(record, WorkspaceToolSummaries.Summarize(result), WorkspaceToolSummaries.Describe(result));
            return result;
        }
        catch (Exception exception)
        {
            _observer.Fail(record, exception.Message);
            throw;
        }
    }

    public async Task<ShellCommandResult> RunShellCommandAsync(
        string command,
        int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        var argumentsJson = JsonSerializer.Serialize(new
        {
            command,
            timeoutSeconds
        });
        var record = _observer.Start("run_shell_command", argumentsJson);

        try
        {
            if (RequiresApproval)
            {
                record = _observer.AwaitApproval(record, "Waiting for your confirmation in the activity panel.");
                if (!await RequestApprovalAsync(
                        new ToolApprovalRequest(
                            record.Id,
                            "run_shell_command",
                            "Run PowerShell Command",
                            $"Allow the agent to run this PowerShell command in '{_workspaceRoot.RootPath}'?\n\nTimeout: {timeoutSeconds} seconds\n\nCommand:\n{command}",
                            argumentsJson),
                        cancellationToken))
                {
                    var denied = new ShellCommandResult(
                        command,
                        false,
                        null,
                        string.Empty,
                        string.Empty,
                        false,
                        "User denied approval.");
                    _observer.Cancel(record, WorkspaceToolSummaries.Summarize(denied));
                    return denied;
                }

                record = _observer.Resume(record, "Approval granted. Running PowerShell command...");
            }

            var result = await _workspaceToolService.RunShellCommandAsync(_workspaceRoot.RootPath, command, timeoutSeconds, cancellationToken);
            _observer.Complete(record, WorkspaceToolSummaries.Summarize(result), WorkspaceToolSummaries.Describe(result));
            return result;
        }
        catch (Exception exception)
        {
            _observer.Fail(record, exception.Message);
            throw;
        }
    }

    private async Task<bool> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        if (_toolPermissionMode == ToolPermissionMode.FullAccess)
        {
            return true;
        }

        if (_toolApprovalHandler is null)
        {
            throw new InvalidOperationException("This tool call requires human approval, but no approval handler is available.");
        }

        return await _toolApprovalHandler.RequestApprovalAsync(request, cancellationToken);
    }

    private bool RequiresApproval => _toolPermissionMode != ToolPermissionMode.FullAccess;

    private async Task<T> ExecuteObservedAsync<T>(
        string toolName,
        string argumentsJson,
        Func<Task<T>> action,
        Func<T, string> summarize,
        Func<T, string> describe)
    {
        var record = _observer.Start(toolName, argumentsJson);

        try
        {
            var result = await action();
            _observer.Complete(record, summarize(result), describe(result));
            return result;
        }
        catch (Exception exception)
        {
            _observer.Fail(record, exception.Message);
            throw;
        }
    }
}
