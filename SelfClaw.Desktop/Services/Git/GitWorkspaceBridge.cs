using System.IO;
using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Desktop.Services.Git;

internal sealed class GitWorkspaceBridge
{
    private readonly IWorkspaceSelectionController _selectionController;
    private readonly IGitWorkspaceQuery _workspaceQuery;
    private readonly IGitWorkspaceManager _workspaceManager;
    private readonly IGitMergeManager _mergeManager;
    private readonly IGitWorkspaceStore _workspaceStore;

    public GitWorkspaceBridge(
        IWorkspaceSelectionController selectionController,
        IGitWorkspaceQuery workspaceQuery,
        IGitWorkspaceManager workspaceManager,
        IGitMergeManager mergeManager,
        IGitWorkspaceStore workspaceStore)
    {
        _selectionController = selectionController;
        _workspaceQuery = workspaceQuery;
        _workspaceManager = workspaceManager;
        _mergeManager = mergeManager;
        _workspaceStore = workspaceStore;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (type is not (
            "get-git-state" or
            "git-create-branch" or
            "git-switch-branch" or
            "git-delete-branch" or
            "git-merge" or
            "git-abort-merge" or
            "git-remove-worktree" or
            "git-force-remove-worktree" or
            "git-release-worktree"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var workspaceRoot = ResolveWorkspaceRoot(payload);
            GitWorkspaceState state;
            switch (type)
            {
                case "get-git-state":
                    state = await _workspaceQuery.GetStateAsync(workspaceRoot, cancellationToken);
                    break;
                case "git-create-branch":
                    state = await _workspaceManager.CreateBranchAsync(
                        workspaceRoot,
                        ReadRequiredString(payload, "branchName"),
                        ReadOptionalString(payload, "startPoint"),
                        cancellationToken);
                    await _selectionController.ReloadWorkspaceSelectionAsync();
                    break;
                case "git-switch-branch":
                    state = await _workspaceManager.SwitchBranchAsync(
                        workspaceRoot,
                        ReadRequiredString(payload, "branchName"),
                        cancellationToken);
                    await _selectionController.ReloadWorkspaceSelectionAsync();
                    break;
                case "git-delete-branch":
                    state = await _workspaceManager.DeleteBranchAsync(
                        workspaceRoot,
                        ReadRequiredString(payload, "branchName"),
                        cancellationToken);
                    await _selectionController.ReloadWorkspaceSelectionAsync();
                    break;
                case "git-merge":
                {
                    var result = await _mergeManager.MergeAsync(workspaceRoot, cancellationToken);
                    return BuildResponse(
                        requestId,
                        result.State,
                        !result.Succeeded && !result.HasConflicts ? result.Message : null,
                        result.Message,
                        result.HasConflicts,
                        result.Succeeded && !result.HasConflicts);
                }
                case "git-abort-merge":
                    state = await _mergeManager.AbortAsync(workspaceRoot, cancellationToken);
                    break;
                case "git-remove-worktree":
                    await _workspaceManager.RemoveManagedWorktreeAsync(workspaceRoot, cancellationToken);
                    await _selectionController.ReloadWorkspaceSelectionAsync();
                    state = await ReadStateAfterWorktreeRemovalAsync(workspaceRoot, cancellationToken);
                    break;
                case "git-force-remove-worktree":
                    await _workspaceManager.ForceRemoveManagedWorktreeAsync(workspaceRoot, cancellationToken);
                    await _selectionController.ReloadWorkspaceSelectionAsync();
                    state = await ReadStateAfterWorktreeRemovalAsync(workspaceRoot, cancellationToken);
                    break;
                case "git-release-worktree":
                {
                    var checkout = await _workspaceStore.GetCheckoutAsync(workspaceRoot.Id, cancellationToken)
                        ?? throw new InvalidOperationException("当前工作目录不是 SelfClaw 工作树。");
                    if (checkout.OwnerConversationId is not Guid ownerConversationId)
                    {
                        throw new InvalidOperationException("当前工作树没有绑定会话。");
                    }

                    await _workspaceStore.ReleaseConversationAsync(ownerConversationId, cancellationToken);
                    state = await _workspaceQuery.GetStateAsync(workspaceRoot, cancellationToken);
                    break;
                }
                default:
                    throw new InvalidOperationException("Unsupported Git operation.");
            }

            return BuildResponse(requestId, state, null, null, state.HasMergeConflicts, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return BuildResponse(requestId, null, exception.Message, null, false, false);
        }
    }

    // The removed worktree's directory is gone, so its state can no longer be read. Report the
    // repository state that stays meaningful for the panel: the newly selected workspace root.
    private async Task<GitWorkspaceState> ReadStateAfterWorktreeRemovalAsync(
        WorkspaceRoot removedWorktree,
        CancellationToken cancellationToken)
    {
        var fallback = _selectionController.SelectedWorkspaceRoot;
        if (fallback is not null &&
            !string.Equals(
                NormalizePath(fallback.RootPath),
                NormalizePath(removedWorktree.RootPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return await _workspaceQuery.GetStateAsync(fallback, cancellationToken);
        }

        // The removed worktree was still the selected root; the panel falls back to the git state
        // it already has until the selection is reloaded.
        return await _workspaceQuery.GetStateAsync(removedWorktree, cancellationToken);
    }

    // Worktree actions carry the path of the row that was clicked, so they must not fall back to
    // the selected workspace root: the main checkout is not a managed worktree and every managed
    // guard would reject it. Actions without a path keep the selected-root behaviour.
    private WorkspaceRoot ResolveWorkspaceRoot(JsonElement payload)
    {
        var path = ReadOptionalString(payload, "workspaceRootPath");
        if (path is null)
        {
            return _selectionController.SelectedWorkspaceRoot
                ?? throw new InvalidOperationException("请先选择一个工作目录。");
        }

        var normalized = NormalizePath(path);
        var match = _selectionController.WorkspaceRoots.FirstOrDefault(root =>
            string.Equals(NormalizePath(root.RootPath), normalized, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException($"找不到工作树“{path}”，请刷新后重试。");
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(Path.TrimEndingDirectorySeparator(path.Trim()));

    private static object BuildResponse(
        string? requestId,
        GitWorkspaceState? state,
        string? error,
        string? message,
        bool hasConflicts,
        bool succeeded)
        => new
        {
            type = "git-state",
            requestId,
            succeeded,
            error,
            message,
            hasConflicts,
            state
        };

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
