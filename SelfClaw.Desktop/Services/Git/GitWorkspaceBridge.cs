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
            "git-release-worktree"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            var workspaceRoot = _selectionController.SelectedWorkspaceRoot
                ?? throw new InvalidOperationException("请先选择一个工作目录。");
            GitWorkspaceState state;
            switch (type)
            {
                case "get-git-state":
                    state = await _workspaceQuery.GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
                    break;
                case "git-create-branch":
                    state = await _workspaceManager.CreateBranchAsync(
                        workspaceRoot,
                        ReadRequiredString(payload, "branchName"),
                        ReadOptionalString(payload, "startPoint"),
                        cancellationToken).ConfigureAwait(false);
                    await _selectionController.ReloadWorkspaceSelectionAsync().ConfigureAwait(false);
                    break;
                case "git-switch-branch":
                    state = await _workspaceManager.SwitchBranchAsync(
                        workspaceRoot,
                        ReadRequiredString(payload, "branchName"),
                        cancellationToken).ConfigureAwait(false);
                    await _selectionController.ReloadWorkspaceSelectionAsync().ConfigureAwait(false);
                    break;
                case "git-delete-branch":
                    state = await _workspaceManager.DeleteBranchAsync(
                        workspaceRoot,
                        ReadRequiredString(payload, "branchName"),
                        cancellationToken).ConfigureAwait(false);
                    await _selectionController.ReloadWorkspaceSelectionAsync().ConfigureAwait(false);
                    break;
                case "git-merge":
                {
                    var result = await _mergeManager.MergeAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
                    return BuildResponse(
                        requestId,
                        result.State,
                        !result.Succeeded && !result.HasConflicts ? result.Message : null,
                        result.Message,
                        result.HasConflicts,
                        result.Succeeded && !result.HasConflicts);
                }
                case "git-abort-merge":
                    state = await _mergeManager.AbortAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
                    break;
                case "git-remove-worktree":
                    await _workspaceManager.RemoveManagedWorktreeAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
                    await _selectionController.ReloadWorkspaceSelectionAsync().ConfigureAwait(false);
                    state = await _workspaceQuery.GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
                    break;
                case "git-release-worktree":
                {
                    var checkout = await _workspaceStore.GetCheckoutAsync(workspaceRoot.Id, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("当前工作目录不是 SelfClaw 工作树。");
                    if (checkout.OwnerConversationId is not Guid ownerConversationId)
                    {
                        throw new InvalidOperationException("当前工作树没有绑定会话。");
                    }

                    await _workspaceStore.ReleaseConversationAsync(ownerConversationId, cancellationToken).ConfigureAwait(false);
                    state = await _workspaceQuery.GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
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
