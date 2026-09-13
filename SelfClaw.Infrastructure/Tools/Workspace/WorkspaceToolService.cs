using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal sealed class WorkspaceToolService : IWorkspaceToolService
{
    private readonly WorkspaceFileService _files;
    private readonly WorkspaceSearchService _search;
    private readonly WorkspaceShellRunner _shell;
    private readonly ILogger<WorkspaceToolService> _logger;

    public WorkspaceToolService(WorkspaceFileService files, WorkspaceSearchService search,
        WorkspaceShellRunner shell, ILogger<WorkspaceToolService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(shell);
        _files = files;
        _search = search;
        _shell = shell;
        _logger = logger ?? NullLogger<WorkspaceToolService>.Instance;
    }

    public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
        string workspaceRootPath,
        string? relativePath,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            "list workspace files",
            workspaceRootPath,
            () => _files.ListFilesAsync(workspaceRootPath, relativePath, cancellationToken),
            ("RelativePath", relativePath ?? string.Empty));

    public async Task<WorkspaceFileContent> ReadFileAsync(
        string workspaceRootPath,
        string relativePath,
        int? startLine = null,
        int? lineCount = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "read workspace file",
            workspaceRootPath,
            () => _files.ReadFileAsync(workspaceRootPath, relativePath, startLine, lineCount, cancellationToken),
            ("RelativePath", relativePath),
            ("StartLine", startLine),
            ("LineCount", lineCount)).ConfigureAwait(false);
    }

    public async Task<WorkspaceFileWriteResult> WriteFileAsync(
        string workspaceRootPath,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "write workspace file",
            workspaceRootPath,
            () => _files.WriteFileAsync(workspaceRootPath, relativePath, content, cancellationToken),
            ("RelativePath", relativePath),
            ("CharacterCount", content?.Length ?? 0)).ConfigureAwait(false);
    }

    public async Task<WorkspaceFileWriteResult> EditFileAsync(
        string workspaceRootPath,
        string relativePath,
        string oldText,
        string newText,
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "edit workspace file",
            workspaceRootPath,
            () => _files.EditFileAsync(workspaceRootPath, relativePath, oldText, newText, replaceAll, cancellationToken),
            ("RelativePath", relativePath),
            ("ReplaceAll", replaceAll)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
        string workspaceRootPath,
        string query,
        WorkspaceSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "search workspace text",
            workspaceRootPath,
            () => _search.SearchTextAsync(workspaceRootPath, query, options, cancellationToken),
            ("QueryLength", query?.Length ?? 0),
            ("Glob", options?.Glob ?? string.Empty),
            ("IsRegex", options?.IsRegex ?? false)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(
        string workspaceRootPath,
        string pattern,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            "glob workspace files",
            workspaceRootPath,
            () => _search.GlobFilesAsync(workspaceRootPath, pattern, relativePath, cancellationToken),
            ("Pattern", pattern),
            ("RelativePath", relativePath ?? string.Empty));

    public async Task<ShellCommandResult> RunShellCommandAsync(
        string workspaceRootPath,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
            "run workspace shell command",
            workspaceRootPath,
            () => _shell.RunShellCommandAsync(workspaceRootPath, command, timeoutSeconds, cancellationToken),
            ("TimeoutSeconds", timeoutSeconds),
            ("CommandLength", command?.Length ?? 0)).ConfigureAwait(false);

        if (result.ExitCode is int exitCode && exitCode != 0)
        {
            _logger.LogWarning(
                "Workspace PowerShell command exited with a non-zero code. WorkspaceRoot={WorkspaceRoot}, ExitCode={ExitCode}",
                workspaceRootPath,
                exitCode);
        }

        return result;
    }

    private async Task<T> ExecuteAsync<T>(
        string operationName,
        string workspaceRootPath,
        Func<Task<T>> action,
        params (string Name, object? Value)[] properties)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Workspace operation canceled. Operation={Operation}, WorkspaceRoot={WorkspaceRoot}, Details={Details}",
                operationName,
                workspaceRootPath,
                FormatProperties(properties));
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Workspace operation failed. Operation={Operation}, WorkspaceRoot={WorkspaceRoot}, Details={Details}",
                operationName,
                workspaceRootPath,
                FormatProperties(properties));
            throw;
        }
    }

    private static string FormatProperties(IEnumerable<(string Name, object? Value)> properties)
        => string.Join(
            ", ",
            properties.Select(property => $"{property.Name}={property.Value ?? "<null>"}"));
}
