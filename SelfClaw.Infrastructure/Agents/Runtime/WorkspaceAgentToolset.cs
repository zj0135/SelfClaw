using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed class WorkspaceAgentToolset
{
    internal const string DeniedResult = "User denied this tool call.";

    private readonly IWorkspaceToolService _workspaceTools;

    public WorkspaceAgentToolset(IWorkspaceToolService workspaceTools)
    {
        _workspaceTools = workspaceTools;
    }

    public IReadOnlyList<AITool> CreateTools(
        WorkspaceRoot workspaceRoot,
        Guid conversationId,
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        var bound = new BoundWorkspaceTools(
            _workspaceTools,
            workspaceRoot.RootPath,
            conversationId,
            permissionMode,
            approvalHandler);

        return
        [
            AIFunctionFactory.Create(
                (Func<string?, CancellationToken, Task<IReadOnlyList<WorkspaceFileEntry>>>)bound.ListFilesAsync,
                "list_files",
                "List files and directories at a path inside the current workspace."),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<IReadOnlyList<WorkspaceSearchHit>>>)bound.SearchTextAsync,
                "search_text",
                "Search text files in the current workspace for a case-insensitive query."),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<WorkspaceFileContent>>)bound.ReadFileAsync,
                "read_file",
                "Read a UTF-8 text file inside the current workspace."),
            AIFunctionFactory.Create(
                (Func<string, string, CancellationToken, Task<object>>)bound.WriteFileAsync,
                "write_file",
                "Create or overwrite a UTF-8 text file inside the current workspace."),
            AIFunctionFactory.Create(
                (Func<string, int, CancellationToken, Task<object>>)bound.RunShellCommandAsync,
                "run_shell_command",
                "Run a PowerShell command with the current workspace as its working directory.")
        ];
    }

    private sealed class BoundWorkspaceTools
    {
        private readonly IWorkspaceToolService _workspaceTools;
        private readonly string _workspaceRootPath;
        private readonly Guid _conversationId;
        private readonly ToolPermissionMode _permissionMode;
        private readonly IToolApprovalHandler? _approvalHandler;

        public BoundWorkspaceTools(
            IWorkspaceToolService workspaceTools,
            string workspaceRootPath,
            Guid conversationId,
            ToolPermissionMode permissionMode,
            IToolApprovalHandler? approvalHandler)
        {
            _workspaceTools = workspaceTools;
            _workspaceRootPath = workspaceRootPath;
            _conversationId = conversationId;
            _permissionMode = permissionMode;
            _approvalHandler = approvalHandler;
        }

        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
            [Description("Workspace-relative directory path. Use an empty value for the workspace root.")] string? relativePath,
            CancellationToken cancellationToken)
            => _workspaceTools.ListFilesAsync(_workspaceRootPath, relativePath, cancellationToken);

        public Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
            [Description("Case-insensitive text to search for across workspace text files.")] string query,
            CancellationToken cancellationToken)
            => _workspaceTools.SearchTextAsync(_workspaceRootPath, query, cancellationToken);

        public Task<WorkspaceFileContent> ReadFileAsync(
            [Description("Path to a text file, relative to the workspace root.")] string relativePath,
            CancellationToken cancellationToken)
            => _workspaceTools.ReadFileAsync(_workspaceRootPath, relativePath, cancellationToken);

        public async Task<object> WriteFileAsync(
            [Description("Destination path, relative to the workspace root.")] string relativePath,
            [Description("Complete UTF-8 text content to write to the file.")] string content,
            CancellationToken cancellationToken)
        {
            var argumentsJson = JsonSerializer.Serialize(new { relativePath, content });
            if (!await IsApprovedAsync(
                    "write_file",
                    "Write workspace file",
                    "Create or overwrite a text file in the current workspace.",
                    argumentsJson,
                    cancellationToken))
            {
                return DeniedResult;
            }

            return await _workspaceTools.WriteFileAsync(
                _workspaceRootPath,
                relativePath,
                content,
                cancellationToken);
        }

        public async Task<object> RunShellCommandAsync(
            [Description("PowerShell command to run in the workspace root.")] string command,
            [Description("Maximum execution time in seconds, from 1 to 600.")] int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var argumentsJson = JsonSerializer.Serialize(new { command, timeoutSeconds });
            if (!await IsApprovedAsync(
                    "run_shell_command",
                    "Run workspace shell command",
                    "Execute a PowerShell command in the current workspace.",
                    argumentsJson,
                    cancellationToken))
            {
                return DeniedResult;
            }

            return await _workspaceTools.RunShellCommandAsync(
                _workspaceRootPath,
                command,
                timeoutSeconds,
                cancellationToken);
        }

        private async Task<bool> IsApprovedAsync(
            string toolName,
            string displayName,
            string description,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            if (_permissionMode == ToolPermissionMode.FullAccess)
            {
                return true;
            }

            if (_approvalHandler is null)
            {
                return false;
            }

            return await _approvalHandler.RequestApprovalAsync(
                new ToolApprovalRequest(
                    Guid.NewGuid(),
                    toolName,
                    displayName,
                    description,
                    argumentsJson,
                    _conversationId),
                cancellationToken);
        }
    }
}
