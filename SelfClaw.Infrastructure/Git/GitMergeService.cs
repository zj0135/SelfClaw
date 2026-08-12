using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Git;

internal sealed class GitMergeService : IGitMergeManager
{
    private readonly GitCommandRunner _runner;
    private readonly IGitWorkspaceStore _store;
    private readonly IConversationRepository _conversationRepository;
    private readonly IGitWorkspaceQuery _workspaceQuery;

    public GitMergeService(
        GitCommandRunner runner,
        IGitWorkspaceStore store,
        IConversationRepository conversationRepository,
        IGitWorkspaceQuery workspaceQuery)
    {
        _runner = runner;
        _store = store;
        _conversationRepository = conversationRepository;
        _workspaceQuery = workspaceQuery;
    }

    public async Task<GitMergeResult> MergeAsync(
        WorkspaceRoot managedWorktree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managedWorktree);
        var checkout = await _store.GetCheckoutAsync(managedWorktree.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected workspace is not a managed Git worktree.");
        if (!checkout.IsManaged || checkout.SourceWorkspaceRootId is not Guid sourceId)
        {
            throw new InvalidOperationException("The selected workspace is not a managed Git worktree.");
        }

        var managedState = await _workspaceQuery.GetStateAsync(managedWorktree, cancellationToken).ConfigureAwait(false);
        if (!managedState.IsRepository)
        {
            throw new InvalidOperationException(managedState.Error ?? "The managed worktree is not available.");
        }

        if (managedState.IsDirty)
        {
            return new GitMergeResult(false, false, "Commit or discard worktree changes before merging.", managedState);
        }

        var source = (await _conversationRepository.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == sourceId)
            ?? throw new InvalidOperationException("The base workspace for this worktree no longer exists.");
        var sourceState = await _workspaceQuery.GetStateAsync(source, cancellationToken).ConfigureAwait(false);
        if (!sourceState.IsRepository || sourceState.BranchName is null ||
            !string.Equals(sourceState.BranchName, checkout.BaseBranchName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The base checkout must be on the recorded base branch before merging.");
        }

        var merge = await _runner.RunAsync(
            source.RootPath,
            ["merge", "--no-edit", checkout.BranchName],
            cancellationToken).ConfigureAwait(false);
        var sourceAfterMerge = await _workspaceQuery.GetStateAsync(source, cancellationToken).ConfigureAwait(false);
        var conflicts = sourceAfterMerge.HasMergeConflicts;
        var message = string.IsNullOrWhiteSpace(merge.Message)
            ? (merge.Succeeded ? "Merged successfully." : "Git merge failed.")
            : merge.Message;
        return new GitMergeResult(merge.Succeeded && !conflicts, conflicts, message, managedState with
        {
            HasMergeConflicts = conflicts,
            IsDirty = managedState.IsDirty || conflicts
        });
    }

    public async Task<GitWorkspaceState> AbortAsync(
        WorkspaceRoot managedWorktree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managedWorktree);
        var checkout = await _store.GetCheckoutAsync(managedWorktree.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected workspace is not a managed Git worktree.");
        if (!checkout.IsManaged || checkout.SourceWorkspaceRootId is not Guid sourceId)
        {
            throw new InvalidOperationException("The selected workspace is not a managed Git worktree.");
        }

        var source = (await _conversationRepository.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == sourceId)
            ?? throw new InvalidOperationException("The base workspace for this worktree no longer exists.");
        var abort = await _runner.RunAsync(source.RootPath, ["merge", "--abort"], cancellationToken).ConfigureAwait(false);
        if (!abort.Succeeded)
        {
            throw new InvalidOperationException(abort.Message);
        }

        return await _workspaceQuery.GetStateAsync(managedWorktree, cancellationToken).ConfigureAwait(false);
    }
}
