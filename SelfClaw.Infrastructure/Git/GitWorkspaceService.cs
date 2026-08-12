using System.Globalization;
using System.Text.RegularExpressions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Git;

internal sealed class GitWorkspaceService : IGitWorkspaceQuery, IGitWorkspaceManager
{
    private const string RecordSeparator = "\x1e";
    private const string FieldSeparator = "\x1f";
    private readonly GitCommandRunner _runner;
    private readonly IGitWorkspaceStore _store;
    private readonly IConversationRepository _conversationRepository;
    private readonly StoragePaths _storagePaths;

    public GitWorkspaceService(
        GitCommandRunner runner,
        IGitWorkspaceStore store,
        IConversationRepository conversationRepository,
        StoragePaths storagePaths)
    {
        _runner = runner;
        _store = store;
        _conversationRepository = conversationRepository;
        _storagePaths = storagePaths;
    }

    public async Task<GitWorkspaceState> GetStateAsync(
        WorkspaceRoot workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        if (!Directory.Exists(workspaceRoot.RootPath))
        {
            return CreateNotRepository("Workspace directory does not exist.");
        }

        GitCommandResult commonDirectoryResult;
        try
        {
            commonDirectoryResult = await RunGitAsync(
                workspaceRoot.RootPath,
                ["rev-parse", "--git-common-dir"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return CreateNotRepository(exception.Message, isGitAvailable: false);
        }

        if (!commonDirectoryResult.Succeeded)
        {
            return CreateNotRepository(commonDirectoryResult.Message);
        }

        var commonDirectory = ResolveGitPath(workspaceRoot.RootPath, commonDirectoryResult.StandardOutput.Trim());
        var repositoryRootResult = await RunGitAsync(
            workspaceRoot.RootPath,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);
        if (!repositoryRootResult.Succeeded)
        {
            return CreateNotRepository(repositoryRootResult.Message);
        }

        var currentCheckoutRootPath = Path.GetFullPath(repositoryRootResult.StandardOutput.Trim());
        var repositoryRootPath = ResolveRepositoryRootPath(commonDirectory, currentCheckoutRootPath);
        var repositoryId = GitWorkspaceIdentity.RepositoryId(commonDirectory);
        var now = DateTimeOffset.UtcNow;
        var repository = new GitRepositoryRecord(
            repositoryId,
            ResolveRepositoryName(repositoryRootPath),
            commonDirectory,
            now,
            now);

        var checkout = await _store.GetCheckoutAsync(workspaceRoot.Id, cancellationToken).ConfigureAwait(false);
        var branchName = await ReadBranchNameAsync(workspaceRoot.RootPath, cancellationToken).ConfigureAwait(false);
        var headResult = await RunGitAsync(workspaceRoot.RootPath, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!headResult.Succeeded)
        {
            return CreateNotRepository(headResult.Message);
        }

        var isDirty = await IsDirtyAsync(workspaceRoot.RootPath, cancellationToken).ConfigureAwait(false);
        var branches = await ReadBranchesAsync(workspaceRoot.RootPath, branchName, cancellationToken).ConfigureAwait(false);
        var worktrees = await ReadWorktreesAsync(
            workspaceRoot.RootPath,
            workspaceRoot.RootPath,
            cancellationToken).ConfigureAwait(false);
        var selectedBranch = branches.FirstOrDefault(item => item.IsCurrent);
        var (ahead, behind) = await ReadAheadBehindAsync(
            workspaceRoot.RootPath,
            checkout?.IsManaged == true ? checkout.BaseBranchName : selectedBranch?.UpstreamName,
            cancellationToken).ConfigureAwait(false);
        var hasConflicts = await HasMergeConflictsAsync(workspaceRoot.RootPath, cancellationToken).ConfigureAwait(false);

        var isManaged = checkout?.IsManaged == true || workspaceRoot.IsManagedWorktree;
        var ownerConversationId = checkout?.OwnerConversationId ?? workspaceRoot.ManagedConversationId;
        var baseBranchName = checkout?.BaseBranchName ?? workspaceRoot.BaseBranchName;
        checkout = new GitCheckoutRecord(
            workspaceRoot.Id,
            repositoryId,
            isManaged,
            ownerConversationId,
            checkout?.SourceWorkspaceRootId,
            branchName ?? "HEAD",
            baseBranchName,
            checkout?.BaseCommitSha,
            checkout?.CreatedAtUtc ?? now,
            now);
        await _store.SaveAsync(repository, checkout, cancellationToken).ConfigureAwait(false);

        var managedConflict = isManaged && checkout.SourceWorkspaceRootId is Guid sourceId
            ? await ReadSourceMergeConflictAsync(sourceId, cancellationToken).ConfigureAwait(false)
            : false;
        return new GitWorkspaceState(
            true,
            true,
            null,
            repositoryId,
            repository.Name,
            repositoryRootPath,
            branchName,
            headResult.StandardOutput.Trim(),
            branchName is null,
            isDirty,
            ahead,
            behind,
            isManaged,
            ownerConversationId,
            baseBranchName,
            hasConflicts || managedConflict,
            branches,
            worktrees);
    }

    public async Task<ManagedGitWorktreeCreation> CreateManagedWorktreeAsync(
        WorkspaceRoot sourceWorkspace,
        Guid conversationId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceWorkspace);
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("A conversation id is required.", nameof(conversationId));
        }

        var sourceState = await GetStateAsync(sourceWorkspace, cancellationToken).ConfigureAwait(false);
        EnsureRepository(sourceState);
        if (sourceState.IsManagedWorktree || sourceState.BranchName is null)
        {
            throw new InvalidOperationException("A managed worktree must be created from a local branch checkout.");
        }

        var commonDirectoryResult = await RunGitAsync(
            sourceWorkspace.RootPath,
            ["rev-parse", "--git-common-dir"],
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(commonDirectoryResult);
        var commonDirectory = ResolveGitPath(sourceWorkspace.RootPath, commonDirectoryResult.StandardOutput.Trim());
        var existing = await _store.GetConversationCheckoutAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var existingRoot = (await _conversationRepository.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.Id == existing.WorkspaceRootId);
            if (existingRoot is not null)
            {
                return new ManagedGitWorktreeCreation(existingRoot, new GitRepositoryRecord(
                    sourceState.RepositoryId!.Value,
                    sourceState.RepositoryName!,
                    commonDirectory,
                    existing.CreatedAtUtc,
                    DateTimeOffset.UtcNow), existing, sourceState.IsDirty);
            }
        }

        var branchName = CreateTaskBranchName(prompt, conversationId);
        var worktreePath = Path.Combine(
            _storagePaths.WorktreesDirectory,
            sourceState.RepositoryId!.Value.ToString("D"),
            conversationId.ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        if (Directory.Exists(worktreePath) || File.Exists(worktreePath))
        {
            throw new InvalidOperationException($"The managed worktree path already exists: {worktreePath}");
        }

        var createResult = await RunGitAsync(
            sourceWorkspace.RootPath,
            ["worktree", "add", "-b", branchName, worktreePath, sourceState.HeadCommitSha!],
            cancellationToken).ConfigureAwait(false);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(createResult.Message);
        }

        var now = DateTimeOffset.UtcNow;
        var workspaceRoot = new WorkspaceRoot(
            Guid.NewGuid(),
            $"{sourceState.RepositoryName} · {branchName}",
            worktreePath,
            now,
            now,
            sourceState.RepositoryId,
            sourceState.RepositoryName,
            branchName,
            true,
            conversationId,
            sourceState.BranchName);
        var repository = new GitRepositoryRecord(
            sourceState.RepositoryId.Value,
            sourceState.RepositoryName!,
            commonDirectory,
            now,
            now);
        var checkout = new GitCheckoutRecord(
            workspaceRoot.Id,
            repository.Id,
            true,
            conversationId,
            sourceWorkspace.Id,
            branchName,
            sourceState.BranchName,
            sourceState.HeadCommitSha,
            now,
            now);
        try
        {
            await _conversationRepository.UpsertWorkspaceRootAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
            await _store.SaveAsync(repository, checkout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RunGitAsync(sourceWorkspace.RootPath, ["worktree", "remove", "--force", worktreePath], CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new ManagedGitWorktreeCreation(workspaceRoot, repository, checkout, sourceState.IsDirty);
    }

    public async Task<GitWorkspaceState> CreateBranchAsync(
        WorkspaceRoot workspaceRoot,
        string branchName,
        string? startPoint = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBranchName(branchName);
        var state = await GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        EnsureRepository(state);
        var arguments = new List<string> { "branch", branchName };
        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            arguments.Add(startPoint);
        }

        var result = await RunGitAsync(workspaceRoot.RootPath, arguments, cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return await GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitWorkspaceState> SwitchBranchAsync(
        WorkspaceRoot workspaceRoot,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        ValidateBranchName(branchName);
        var state = await GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        EnsureRepository(state);
        var result = await RunGitAsync(workspaceRoot.RootPath, ["switch", branchName], cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return await GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitWorkspaceState> DeleteBranchAsync(
        WorkspaceRoot workspaceRoot,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        ValidateBranchName(branchName);
        var state = await GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        EnsureRepository(state);
        if (state.Worktrees.Any(item => string.Equals(item.BranchName, branchName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The branch is checked out by a worktree and cannot be deleted.");
        }

        var result = await RunGitAsync(workspaceRoot.RootPath, ["branch", "-d", branchName], cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result);
        return await GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveManagedWorktreeAsync(
        WorkspaceRoot workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        var checkout = await _store.GetCheckoutAsync(workspaceRoot.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected workspace is not a managed Git worktree.");
        if (!checkout.IsManaged)
        {
            throw new InvalidOperationException("The selected workspace is not a managed Git worktree.");
        }

        if (!IsPathWithinDirectory(workspaceRoot.RootPath, _storagePaths.WorktreesDirectory))
        {
            throw new InvalidOperationException("Managed worktrees can only be removed from SelfClaw's worktree directory.");
        }

        var state = await GetStateAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        EnsureRepository(state);
        if (!state.Worktrees.Any(item => item.IsCurrent &&
            string.Equals(
                GitWorkspaceIdentity.Normalize(item.Path),
                GitWorkspaceIdentity.Normalize(workspaceRoot.RootPath),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The selected path is not the recorded Git worktree.");
        }
        if (state.IsDirty || state.HasMergeConflicts)
        {
            throw new InvalidOperationException("The worktree has uncommitted changes or conflicts and cannot be removed safely.");
        }

        var source = await ResolveSourceWorkspaceAsync(checkout, cancellationToken).ConfigureAwait(false);
        var sourceState = await GetStateAsync(source, cancellationToken).ConfigureAwait(false);
        EnsureRepository(sourceState);
        if (!string.Equals(sourceState.BranchName, checkout.BaseBranchName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The base checkout must be on the recorded base branch before cleanup.");
        }

        var ancestorResult = await RunGitAsync(
            source.RootPath,
            ["merge-base", "--is-ancestor", checkout.BranchName, checkout.BaseBranchName!],
            cancellationToken).ConfigureAwait(false);
        if (!ancestorResult.Succeeded)
        {
            throw new InvalidOperationException("The task branch has not been merged into its base branch.");
        }

        var removeResult = await RunGitAsync(
            source.RootPath,
            ["worktree", "remove", workspaceRoot.RootPath],
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(removeResult);
        await _store.DeleteCheckoutAsync(workspaceRoot.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkspaceRoot> ResolveSourceWorkspaceAsync(
        GitCheckoutRecord checkout,
        CancellationToken cancellationToken)
        => checkout.SourceWorkspaceRootId is Guid sourceId
            ? (await _conversationRepository.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.Id == sourceId)
                ?? throw new InvalidOperationException("The base workspace for this worktree no longer exists.")
            : throw new InvalidOperationException("The managed worktree has no base workspace.");

    private async Task<bool> ReadSourceMergeConflictAsync(Guid sourceWorkspaceRootId, CancellationToken cancellationToken)
    {
        var source = (await _conversationRepository.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == sourceWorkspaceRootId);
        return source is not null && await HasMergeConflictsAsync(source.RootPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => await _runner.RunAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);

    private static void EnsureRepository(GitWorkspaceState state)
    {
        if (!state.IsGitAvailable)
        {
            throw new InvalidOperationException(state.Error ?? "Git is not available.");
        }

        if (!state.IsRepository)
        {
            throw new InvalidOperationException(state.Error ?? "The selected directory is not a Git repository.");
        }
    }

    private static void EnsureSucceeded(GitCommandResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private static GitWorkspaceState CreateNotRepository(string? error, bool isGitAvailable = true)
        => new(
            isGitAvailable,
            false,
            error,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            null,
            null,
            false,
            null,
            null,
            false,
            [],
            []);

    private static string ResolveGitPath(string workingDirectory, string value)
        => Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(workingDirectory, value));

    private static string ResolveRepositoryName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private static string ResolveRepositoryRootPath(string commonDirectory, string currentCheckoutRootPath)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(commonDirectory));
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(commonDirectory));
        return string.Equals(directoryName, ".git", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(parent)
            ? parent
            : currentCheckoutRootPath;
    }

    private async Task<string?> ReadBranchNameAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(workingDirectory, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken).ConfigureAwait(false);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim()
            : null;
    }

    private async Task<bool> IsDirtyAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(workingDirectory, ["status", "--porcelain=v1", "--untracked-files=normal"], cancellationToken).ConfigureAwait(false);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private async Task<bool> HasMergeConflictsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(workingDirectory, ["status", "--porcelain=v1"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return false;
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Length >= 2 && IsConflictStatus(line.AsSpan(0, 2)));
    }

    private static bool IsConflictStatus(ReadOnlySpan<char> status)
        => status is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";

    private async Task<(int? Ahead, int? Behind)> ReadAheadBehindAsync(
        string workingDirectory,
        string? reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, null);
        }

        var result = await RunGitAsync(
            workingDirectory,
            ["rev-list", "--left-right", "--count", $"HEAD...{reference}"],
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return (null, null);
        }

        var parts = result.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ahead) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var behind)
            ? (ahead, behind)
            : (null, null);
    }

    private async Task<IReadOnlyList<GitBranchInfo>> ReadBranchesAsync(
        string workingDirectory,
        string? currentBranch,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            workingDirectory,
            ["for-each-ref", $"--format=%(refname)%x1f%(refname:short)%x1f%(objectname)%x1f%(upstream:short)%x1f%(HEAD)%x1e", "refs/heads", "refs/remotes"],
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        var branches = new List<GitBranchInfo>();
        foreach (var record in result.StandardOutput.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.TrimEnd('\r', '\n').Split(FieldSeparator);
            if (parts.Length < 5 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            var fullName = parts[0];
            var name = parts[1];
            var isRemote = fullName.StartsWith("refs/remotes/", StringComparison.Ordinal);
            branches.Add(new GitBranchInfo(
                name,
                fullName,
                parts[2],
                string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3],
                isRemote,
                string.Equals(parts[4], "*", StringComparison.Ordinal) ||
                string.Equals(name, currentBranch, StringComparison.Ordinal),
                null));
        }

        var worktrees = await ReadWorktreesAsync(workingDirectory, workingDirectory, cancellationToken).ConfigureAwait(false);
        return branches
            .Select(branch => branch with
            {
                CheckoutPath = worktrees.FirstOrDefault(item => string.Equals(item.BranchName, branch.Name, StringComparison.OrdinalIgnoreCase))?.Path
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<GitWorktreeInfo>> ReadWorktreesAsync(
        string workingDirectory,
        string currentPath,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(workingDirectory, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        var managedCheckouts = new Dictionary<string, GitCheckoutRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in await _conversationRepository.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false))
        {
            var checkout = await _store.GetCheckoutAsync(root.Id, cancellationToken).ConfigureAwait(false);
            if (checkout is not null)
            {
                managedCheckouts[GitWorkspaceIdentity.Normalize(root.RootPath)] = checkout;
            }
        }

        var worktrees = new List<GitWorktreeInfo>();
        string? path = null;
        string? sha = null;
        string? branch = null;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.None))
        {
            if (line.Length == 0)
            {
                if (path is not null && sha is not null)
                {
                    managedCheckouts.TryGetValue(GitWorkspaceIdentity.Normalize(path), out var checkout);
                    worktrees.Add(new GitWorktreeInfo(
                        path,
                        sha,
                        branch,
                        branch is null,
                        GitWorkspaceIdentity.Normalize(path) == GitWorkspaceIdentity.Normalize(currentPath),
                        checkout?.IsManaged == true,
                        checkout?.OwnerConversationId));
                }

                path = null;
                sha = null;
                branch = null;
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line[9..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                sha = line[5..];
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line[18..];
            }
        }

        if (path is not null && sha is not null)
        {
            managedCheckouts.TryGetValue(GitWorkspaceIdentity.Normalize(path), out var checkout);
            worktrees.Add(new GitWorktreeInfo(
                path,
                sha,
                branch,
                branch is null,
                GitWorkspaceIdentity.Normalize(path) == GitWorkspaceIdentity.Normalize(currentPath),
                checkout?.IsManaged == true,
                checkout?.OwnerConversationId));
        }

        return worktrees;
    }

    private static string CreateTaskBranchName(string prompt, Guid conversationId)
    {
        var summary = Regex.Replace(prompt.Trim().ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", "-").Trim('-');
        if (summary.Length > 32)
        {
            summary = summary[..32].Trim('-');
        }

        if (summary.Length == 0)
        {
            summary = "task";
        }

        return $"selfclaw/{summary}-{conversationId.ToString("N")[..8]}";
    }

    private static void ValidateBranchName(string branchName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (branchName.StartsWith("-", StringComparison.Ordinal) || branchName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("The branch name is not valid.", nameof(branchName));
        }
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(Path.TrimEndingDirectorySeparator(directory)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.TrimEndingDirectorySeparator(path)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
