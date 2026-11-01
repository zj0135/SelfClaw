using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Desktop.Services.Git;

internal sealed class GitDiffBridge
{
    private readonly IWorkspaceSelectionController _selectionController;
    private readonly IGitDiffQuery _diffQuery;

    public GitDiffBridge(
        IWorkspaceSelectionController selectionController,
        IGitDiffQuery diffQuery)
    {
        _selectionController = selectionController;
        _diffQuery = diffQuery;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("git-diff/changes" or "git-diff/file"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var workspaceRoot = _selectionController.SelectedWorkspaceRoot
                ?? throw new InvalidOperationException("请先选择一个工作目录。");
            if (type == "git-diff/changes")
            {
                var changes = await _diffQuery.GetChangesAsync(workspaceRoot, cancellationToken);
                return BuildChangesResponse(requestId, workspaceRoot, changes);
            }

            var (scope, diff) = await ReadFileDiffAsync(workspaceRoot, payload, cancellationToken);
            return BuildFileResponse(requestId, workspaceRoot, scope, diff);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return BuildFailureResponse(type, requestId, exception.Message);
        }
    }

    private async Task<(GitChangeScope Scope, GitFileDiff Diff)> ReadFileDiffAsync(
        WorkspaceRoot workspaceRoot,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var scope = ReadOptionalString(payload, "scope") switch
        {
            "staged" => GitChangeScope.Staged,
            "unstaged" => GitChangeScope.Unstaged,
            _ => throw new ArgumentException("scope must be 'unstaged' or 'staged'.")
        };
        var kind = ReadOptionalString(payload, "kind") is { } kindName &&
                   Enum.TryParse<GitChangeKind>(kindName, ignoreCase: true, out var parsedKind)
            ? parsedKind
            : GitChangeKind.Modified;
        var diff = await _diffQuery.GetFileDiffAsync(
            workspaceRoot,
            scope,
            ReadRequiredString(payload, "path"),
            ReadOptionalString(payload, "oldPath"),
            kind,
            cancellationToken);
        return (scope, diff);
    }

    // Not-a-repository is a designed empty state, not an error: the envelope stays resolvable and
    // carries isRepository/isGitAvailable for the pane to render.
    private static object BuildChangesResponse(
        string? requestId,
        WorkspaceRoot workspaceRoot,
        GitWorkspaceChanges changes)
        => new
        {
            type = "git-diff/changes",
            requestId,
            ok = true,
            workspaceRootId = workspaceRoot.Id.ToString("D"),
            workspacePath = workspaceRoot.RootPath,
            repositoryRootPath = changes.RepositoryRootPath,
            branchName = changes.BranchName,
            isGitAvailable = changes.IsGitAvailable,
            isRepository = changes.IsRepository,
            unstaged = BuildChangeSet(changes.Unstaged),
            staged = BuildChangeSet(changes.Staged)
        };

    private static object BuildChangeSet(GitChangeSet set)
        => new
        {
            atFileLimit = set.AtFileLimit,
            files = set.Files.Select(file => new
            {
                path = file.Path,
                oldPath = file.OldPath,
                kind = ToWireKind(file.Kind),
                addedLines = file.AddedLines,
                removedLines = file.RemovedLines,
                isBinary = file.IsBinary
            }).ToArray()
        };

    private static object BuildFileResponse(
        string? requestId,
        WorkspaceRoot workspaceRoot,
        GitChangeScope scope,
        GitFileDiff diff)
        => new
        {
            type = "git-diff/file",
            requestId,
            ok = true,
            workspaceRootId = workspaceRoot.Id.ToString("D"),
            scope = scope == GitChangeScope.Staged ? "staged" : "unstaged",
            diff.Path,
            diff.OldPath,
            kind = ToWireKind(diff.Kind),
            diff.IsBinary,
            diff.Patch,
            diff.Truncated,
            diff.AddedLines,
            diff.RemovedLines
        };

    private static object BuildFailureResponse(string type, string? requestId, string error)
        => new
        {
            type,
            requestId,
            ok = false,
            error
        };

    private static string ToWireKind(GitChangeKind kind) => kind.ToString().ToLowerInvariant();

    private static string ReadRequiredString(JsonElement payload, string propertyName)
        => ReadOptionalString(payload, propertyName)
            ?? throw new ArgumentException($"The {propertyName} value is required.", propertyName);

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
