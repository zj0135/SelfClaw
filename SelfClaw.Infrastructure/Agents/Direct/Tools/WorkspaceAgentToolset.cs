using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

internal sealed class WorkspaceAgentToolset
{
    private readonly IWorkspaceToolService _workspaceTools;

    public WorkspaceAgentToolset(IWorkspaceToolService workspaceTools)
    {
        _workspaceTools = workspaceTools;
    }

    public IReadOnlyList<DirectToolBinding> CreateTools(WorkspaceRoot workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        var bound = new BoundWorkspaceTools(
            _workspaceTools,
            workspaceRoot.RootPath);

        return
        [
            Bind<IReadOnlyList<WorkspaceFileEntry>>(
                bound.ListFilesAsync,
                "list_files",
                "List files and directories at a path inside the current workspace.",
                ToolCallKind.List, FormatEntries),
            Bind<IReadOnlyList<WorkspaceFileEntry>>(
                bound.GlobFilesAsync,
                "glob_files",
                "Find files anywhere in the workspace by glob pattern, e.g. \"**/*.cs\" or \"src/**/test_*.ts\". "
                    + "Returns matching file paths without reading their contents.",
                ToolCallKind.List, FormatEntries),
            Bind<IReadOnlyList<WorkspaceSearchHit>>(
                bound.SearchTextAsync,
                "search_text",
                "Search text files in the current workspace. Backed by ripgrep when available (honours .gitignore). "
                    + "Supply a glob to scope by path, set isRegex for a regular-expression query, "
                    + "and caseSensitive to match case exactly.",
                ToolCallKind.Search, FormatHits),
            Bind<WorkspaceFileContent>(
                bound.ReadFileAsync,
                "read_file",
                "Read a UTF-8 text file inside the current workspace. Omit startLine/lineCount to read from the "
                    + "top, or supply them to page through a large file by line range. The result reports the "
                    + "line range returned and the file's total line count.",
                ToolCallKind.Read, FormatFile),
            Bind<WorkspaceFileWriteResult>(bound.WriteFileAsync, "write_file",
                "Create or overwrite a UTF-8 text file inside the current workspace.",
                ToolCallKind.Edit, FormatWrite, "Write workspace file", requiresApproval: true),
            Bind<WorkspaceFileWriteResult>(bound.EditFileAsync, "edit_file",
                "Edit an existing text file by replacing an exact oldText snippet with newText, without rewriting " +
                "the whole file. oldText must match exactly once unless replaceAll is set. Prefer this over " +
                "write_file for changes to large files.",
                ToolCallKind.Edit, FormatWrite, "Edit workspace file", requiresApproval: true),
            Bind<ShellCommandResult>(bound.RunShellCommandAsync, "run_shell_command",
                "Run a PowerShell command with the current workspace as its working directory.",
                ToolCallKind.Run, FormatShell, "Run workspace shell command", requiresApproval: true)
        ];
    }

    private static DirectToolBinding Bind<TResult>(
        Delegate method, string name, string description, ToolCallKind kind,
        Func<TResult, DirectToolResult> format, string? displayName = null, bool requiresApproval = false)
        => new(AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            ExcludeResultSchema = true,
            MarshalResult = (value, _, _) => value is TResult result
                ? ValueTask.FromResult<object?>(format(result))
                : throw new InvalidOperationException($"Tool '{name}' returned an invalid result.")
        }), new DirectToolDescriptor(name, kind, ToolSourceKind.BuiltIn, DisplayName: displayName), requiresApproval);

    private static DirectToolResult FormatEntries(IReadOnlyList<WorkspaceFileEntry> entries)
        => new(ToolCallStatus.Completed, WorkspaceToolSummaries.Summarize(entries),
            JsonSerializer.SerializeToElement(entries), WorkspaceToolSummaries.Describe(entries));

    private static DirectToolResult FormatHits(IReadOnlyList<WorkspaceSearchHit> hits)
        => new(ToolCallStatus.Completed, WorkspaceToolSummaries.Summarize(hits),
            JsonSerializer.SerializeToElement(hits), WorkspaceToolSummaries.Describe(hits));

    private static DirectToolResult FormatFile(WorkspaceFileContent file)
        => new(ToolCallStatus.Completed, WorkspaceToolSummaries.Summarize(file),
            JsonSerializer.SerializeToElement(file), WorkspaceToolSummaries.Describe(file));

    private static DirectToolResult FormatWrite(WorkspaceFileWriteResult write)
        => new(write.Applied ? ToolCallStatus.Completed : ToolCallStatus.Failed,
            WorkspaceToolSummaries.Summarize(write), JsonSerializer.SerializeToElement(write), WorkspaceToolSummaries.Describe(write));

    private static DirectToolResult FormatShell(ShellCommandResult shell)
        => new(shell.Executed && shell.ExitCode == 0 ? ToolCallStatus.Completed : ToolCallStatus.Failed,
            WorkspaceToolSummaries.Summarize(shell), JsonSerializer.SerializeToElement(shell), WorkspaceToolSummaries.Describe(shell));

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
            [Description("Glob pattern to match workspace-relative file paths, e.g. \"**/*.cs\" or \"src/**/test_*.ts\". relativePath narrows traversal without changing the pattern root.")] string pattern,
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

        public Task<WorkspaceFileWriteResult> WriteFileAsync(
            [Description("Destination path, relative to the workspace root.")] string relativePath,
            [Description("Complete UTF-8 text content to write to the file.")] string content,
            CancellationToken cancellationToken)
            => _workspaceTools.WriteFileAsync(
                _workspaceRootPath,
                relativePath,
                content,
                cancellationToken);

        public Task<WorkspaceFileWriteResult> EditFileAsync(
            [Description("Path to the text file to edit, relative to the workspace root.")] string relativePath,
            [Description("Exact existing text to find. Include enough surrounding context to match a single location unless replaceAll is set.")] string oldText,
            [Description("Replacement text to substitute for oldText.")] string newText,
            [Description("Replace every occurrence of oldText. When false, oldText must match exactly one location.")] bool replaceAll,
            CancellationToken cancellationToken)
            => _workspaceTools.EditFileAsync(
                _workspaceRootPath,
                relativePath,
                oldText,
                newText,
                replaceAll,
                cancellationToken);

        public Task<ShellCommandResult> RunShellCommandAsync(
            [Description("PowerShell command to run in the workspace root.")] string command,
            [Description("Maximum execution time in seconds, from 1 to 600.")] int timeoutSeconds,
            CancellationToken cancellationToken)
            => _workspaceTools.RunShellCommandAsync(
                _workspaceRootPath,
                command,
                timeoutSeconds,
                cancellationToken);

    }
}
