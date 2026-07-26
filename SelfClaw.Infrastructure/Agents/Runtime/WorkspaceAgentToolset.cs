using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Runtime;

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
            workspaceRoot.RootPath);

        var writeFile = AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<object>>)bound.WriteFileAsync,
            "write_file",
            "Create or overwrite a UTF-8 text file inside the current workspace.");
        var editFile = AIFunctionFactory.Create(
            bound.EditFileAsync,
            "edit_file",
            "Edit an existing text file by replacing an exact oldText snippet with newText, without rewriting "
                + "the whole file. oldText must match exactly once unless replaceAll is set. Prefer this over "
                + "write_file for changes to large files.");
        var runShellCommand = AIFunctionFactory.Create(
            (Func<string, int, CancellationToken, Task<object>>)bound.RunShellCommandAsync,
            "run_shell_command",
            "Run a PowerShell command with the current workspace as its working directory.");

        return
        [
            AIFunctionFactory.Create(
                (Func<string?, CancellationToken, Task<IReadOnlyList<WorkspaceFileEntry>>>)bound.ListFilesAsync,
                "list_files",
                "List files and directories at a path inside the current workspace."),
            AIFunctionFactory.Create(
                bound.GlobFilesAsync,
                "glob_files",
                "Find files anywhere in the workspace by glob pattern, e.g. \"**/*.cs\" or \"src/**/test_*.ts\". "
                    + "Returns matching file paths without reading their contents."),
            AIFunctionFactory.Create(
                bound.SearchTextAsync,
                "search_text",
                "Search text files in the current workspace. Backed by ripgrep when available (honours .gitignore). "
                    + "Supply a glob to scope by path, set isRegex for a regular-expression query, "
                    + "and caseSensitive to match case exactly."),
            AIFunctionFactory.Create(
                bound.ReadFileAsync,
                "read_file",
                "Read a UTF-8 text file inside the current workspace. Omit startLine/lineCount to read from the "
                    + "top, or supply them to page through a large file by line range. The result reports the "
                    + "line range returned and the file's total line count."),
            WrapApproval(writeFile, conversationId, permissionMode, approvalHandler, "Write workspace file"),
            WrapApproval(editFile, conversationId, permissionMode, approvalHandler, "Edit workspace file"),
            WrapApproval(runShellCommand, conversationId, permissionMode, approvalHandler, "Run workspace shell command")
        ];
    }

    private static ApprovedAIFunction WrapApproval(
        AIFunction function,
        Guid conversationId,
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler,
        string displayName)
        => new(
            function,
            conversationId,
            permissionMode,
            approvalHandler,
            displayName,
            ToolSourceKind.BuiltIn);

    private sealed class BoundWorkspaceTools
    {
        private readonly IWorkspaceToolService _workspaceTools;
        private readonly string _workspaceRootPath;

        public BoundWorkspaceTools(
            IWorkspaceToolService workspaceTools,
            string workspaceRootPath)
        {
            _workspaceTools = workspaceTools;
            _workspaceRootPath = workspaceRootPath;
        }

        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
            [Description("Workspace-relative directory path. Use an empty value for the workspace root.")] string? relativePath,
            CancellationToken cancellationToken)
            => _workspaceTools.ListFilesAsync(_workspaceRootPath, relativePath, cancellationToken);

        public Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(
            [Description("Glob pattern to match files against, e.g. \"**/*.cs\" or \"src/**/test_*.ts\". Matches file paths relative to the search root.")] string pattern,
            [Description("Optional workspace-relative directory to scope the search to. Leave empty to search from the workspace root.")] string? relativePath,
            CancellationToken cancellationToken)
            => _workspaceTools.GlobFilesAsync(_workspaceRootPath, pattern, relativePath, cancellationToken);

        public Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
            [Description("Text to search for across workspace text files. Treated as a literal substring unless isRegex is true.")] string query,
            [Description("Optional glob limiting which files are searched, e.g. \"src/**/*.cs\" or \"*.md\". Leave empty to search every text file.")] string? glob,
            [Description("Treat the query as a regular expression instead of a literal substring.")] bool isRegex,
            [Description("Match case exactly. Defaults to case-insensitive when false.")] bool caseSensitive,
            [Description("Maximum number of matching lines to return. Leave unset for the default limit.")] int? maxResults,
            CancellationToken cancellationToken)
            => _workspaceTools.SearchTextAsync(
                _workspaceRootPath,
                query,
                new WorkspaceSearchOptions
                {
                    Glob = glob,
                    IsRegex = isRegex,
                    CaseSensitive = caseSensitive,
                    MaxResults = maxResults
                },
                cancellationToken);

        public Task<WorkspaceFileContent> ReadFileAsync(
            [Description("Path to a text file, relative to the workspace root.")] string relativePath,
            [Description("Optional 1-based line to start reading from. Leave unset to read from the top of the file.")] int? startLine,
            [Description("Optional number of lines to return from startLine. Leave unset for the default page size.")] int? lineCount,
            CancellationToken cancellationToken)
            => _workspaceTools.ReadFileAsync(_workspaceRootPath, relativePath, startLine, lineCount, cancellationToken);

        public async Task<object> WriteFileAsync(
            [Description("Destination path, relative to the workspace root.")] string relativePath,
            [Description("Complete UTF-8 text content to write to the file.")] string content,
            CancellationToken cancellationToken)
        {
            return await _workspaceTools.WriteFileAsync(
                _workspaceRootPath,
                relativePath,
                content,
                cancellationToken);
        }

        public async Task<object> EditFileAsync(
            [Description("Path to the text file to edit, relative to the workspace root.")] string relativePath,
            [Description("Exact existing text to find. Include enough surrounding context to match a single location unless replaceAll is set.")] string oldText,
            [Description("Replacement text to substitute for oldText.")] string newText,
            [Description("Replace every occurrence of oldText. When false, oldText must match exactly one location.")] bool replaceAll,
            CancellationToken cancellationToken)
        {
            return await _workspaceTools.EditFileAsync(
                _workspaceRootPath,
                relativePath,
                oldText,
                newText,
                replaceAll,
                cancellationToken);
        }

        public async Task<object> RunShellCommandAsync(
            [Description("PowerShell command to run in the workspace root.")] string command,
            [Description("Maximum execution time in seconds, from 1 to 600.")] int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            return await _workspaceTools.RunShellCommandAsync(
                _workspaceRootPath,
                command,
                timeoutSeconds,
                cancellationToken);
        }

    }
}
