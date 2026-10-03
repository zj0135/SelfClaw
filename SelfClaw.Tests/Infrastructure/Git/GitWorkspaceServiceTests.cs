using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Git;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Git;

public sealed class GitWorkspaceServiceTests
{
    [Fact]
    public async Task Managed_worktree_can_be_committed_merged_and_removed_safely()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testRoot, "repo");
        Directory.CreateDirectory(repositoryPath);
        var storagePaths = StoragePathDefaults.Create(
            Path.Combine(testRoot, "appdata"),
            Path.Combine(testRoot, "appdata", "selfclaw.db"),
            Path.Combine(testRoot, "appdata", "secrets"));

        try
        {
            await RunGitAsync(testRoot, "init", "-b", "main", repositoryPath);
            await RunGitAsync(repositoryPath, "config", "user.email", "tests@selfclaw.local");
            await RunGitAsync(repositoryPath, "config", "user.name", "SelfClaw Tests");
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "README.md"), "base\n");
            await RunGitAsync(repositoryPath, "add", "README.md");
            await RunGitAsync(repositoryPath, "commit", "-m", "initial");
            await RunGitAsync(repositoryPath, "branch", "feature/ui");
            await RunGitAsync(repositoryPath, "update-ref", "refs/remotes/origin/main", "refs/heads/main");

            var database = new SelfClaw.Infrastructure.Data.Sqlite.SqliteDatabase(storagePaths);
            var conversations = new SqliteConversationRepository(database);
            await conversations.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var sourceWorkspace = new WorkspaceRoot(Guid.NewGuid(), "repo", repositoryPath, now, now);
            var store = new SqliteWorkspaceRepository(database);
            await store.UpsertWorkspaceRootAsync(sourceWorkspace);
            var runner = new GitCommandRunner();
            var service = new GitWorkspaceService(runner, store, store, storagePaths);
            var mergeService = new GitMergeService(runner, store, store, service);

            var sourceState = await service.GetStateAsync(sourceWorkspace);
            sourceState.IsRepository.Should().BeTrue();
            sourceState.BranchName.Should().Be("main");
            sourceState.IsDirty.Should().BeFalse();
            sourceState.Branches.Should().Contain(item =>
                item.Name == "main" && !item.IsRemote && item.IsCurrent);
            sourceState.Branches.Should().Contain(item =>
                item.Name == "feature/ui" && !item.IsRemote);
            // Remote-tracking refs follow the local ones in the for-each-ref output, so a newline
            // leaking into the parsed refname used to flag them as local.
            sourceState.Branches.Should().Contain(item =>
                item.Name == "origin/main" && item.FullName == "refs/remotes/origin/main" && item.IsRemote);

            var conversationId = Guid.NewGuid();
            var creation = await service.CreateManagedWorktreeAsync(sourceWorkspace, conversationId, "Add parser tests");
            creation.WorkspaceRoot.IsManagedWorktree.Should().BeTrue();
            creation.Checkout.BranchName.Should().Be("selfclaw/add-parser-tests-" + conversationId.ToString("N")[..8]);
            Directory.Exists(creation.WorkspaceRoot.RootPath).Should().BeTrue();

            await File.AppendAllTextAsync(Path.Combine(creation.WorkspaceRoot.RootPath, "README.md"), "task\n");
            await RunGitAsync(creation.WorkspaceRoot.RootPath, "add", "README.md");
            await RunGitAsync(creation.WorkspaceRoot.RootPath, "commit", "-m", "task change");

            var merge = await mergeService.MergeAsync(creation.WorkspaceRoot);
            merge.Succeeded.Should().BeTrue();
            merge.HasConflicts.Should().BeFalse();
            (await File.ReadAllTextAsync(Path.Combine(repositoryPath, "README.md"))).Should().Contain("task");

            await service.RemoveManagedWorktreeAsync(creation.WorkspaceRoot);
            Directory.Exists(creation.WorkspaceRoot.RootPath).Should().BeFalse();
            (await store.GetCheckoutAsync(creation.WorkspaceRoot.Id)).Should().BeNull();
        }
        finally
        {
            SqliteTestPools.ClearFor(Path.Combine(testRoot, "appdata", "selfclaw.db"));
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task Dirty_worktree_cannot_be_removed_before_merge()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testRoot, "repo");
        Directory.CreateDirectory(repositoryPath);
        var storagePaths = StoragePathDefaults.Create(
            Path.Combine(testRoot, "appdata"),
            Path.Combine(testRoot, "appdata", "selfclaw.db"),
            Path.Combine(testRoot, "appdata", "secrets"));

        try
        {
            await RunGitAsync(testRoot, "init", "-b", "main", repositoryPath);
            await RunGitAsync(repositoryPath, "config", "user.email", "tests@selfclaw.local");
            await RunGitAsync(repositoryPath, "config", "user.name", "SelfClaw Tests");
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "README.md"), "base\n");
            await RunGitAsync(repositoryPath, "add", "README.md");
            await RunGitAsync(repositoryPath, "commit", "-m", "initial");

            var database = new SelfClaw.Infrastructure.Data.Sqlite.SqliteDatabase(storagePaths);
            var conversations = new SqliteConversationRepository(database);
            await conversations.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var sourceWorkspace = new WorkspaceRoot(Guid.NewGuid(), "repo", repositoryPath, now, now);
            var store = new SqliteWorkspaceRepository(database);
            await store.UpsertWorkspaceRootAsync(sourceWorkspace);
            var service = new GitWorkspaceService(new GitCommandRunner(), store, store, storagePaths);
            await service.GetStateAsync(sourceWorkspace);

            var creation = await service.CreateManagedWorktreeAsync(sourceWorkspace, Guid.NewGuid(), "Dirty change");
            await File.AppendAllTextAsync(Path.Combine(creation.WorkspaceRoot.RootPath, "README.md"), "uncommitted\n");

            var act = () => service.RemoveManagedWorktreeAsync(creation.WorkspaceRoot);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*uncommitted*");
            Directory.Exists(creation.WorkspaceRoot.RootPath).Should().BeTrue();
        }
        finally
        {
            SqliteTestPools.ClearFor(Path.Combine(testRoot, "appdata", "selfclaw.db"));
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task Chinese_prompt_produces_ascii_safe_task_branch_that_round_trips()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testRoot, "repo");
        Directory.CreateDirectory(repositoryPath);
        var storagePaths = StoragePathDefaults.Create(
            Path.Combine(testRoot, "appdata"),
            Path.Combine(testRoot, "appdata", "selfclaw.db"),
            Path.Combine(testRoot, "appdata", "secrets"));

        try
        {
            await RunGitAsync(testRoot, "init", "-b", "main", repositoryPath);
            await RunGitAsync(repositoryPath, "config", "user.email", "tests@selfclaw.local");
            await RunGitAsync(repositoryPath, "config", "user.name", "SelfClaw Tests");
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "README.md"), "base\n");
            await RunGitAsync(repositoryPath, "add", "README.md");
            await RunGitAsync(repositoryPath, "commit", "-m", "initial");

            var database = new SelfClaw.Infrastructure.Data.Sqlite.SqliteDatabase(storagePaths);
            var conversations = new SqliteConversationRepository(database);
            await conversations.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var sourceWorkspace = new WorkspaceRoot(Guid.NewGuid(), "repo", repositoryPath, now, now);
            var store = new SqliteWorkspaceRepository(database);
            await store.UpsertWorkspaceRootAsync(sourceWorkspace);
            var service = new GitWorkspaceService(new GitCommandRunner(), store, store, storagePaths);
            await service.GetStateAsync(sourceWorkspace);

            var conversationId = Guid.NewGuid();
            var creation = await service.CreateManagedWorktreeAsync(sourceWorkspace, conversationId, "测试1");

            // The recorded name must stay ASCII so later reads through git stdout match it exactly.
            // "测试1" contributes only the digit "1", which cannot lead a slug, so the branch is
            // task-1-<id>.
            creation.Checkout.BranchName.Should().Be("selfclaw/task-1-" + conversationId.ToString("N")[..8]);
            creation.Checkout.BranchName.Should().MatchRegex("^[A-Za-z0-9/._-]+$");

            // A fresh state read must resolve the very same branch name, which is what the
            // merge-base and worktree-remove guards compare against.
            var reread = await service.GetStateAsync(creation.WorkspaceRoot);
            reread.BranchName.Should().Be(creation.Checkout.BranchName);
            reread.IsManagedWorktree.Should().BeTrue();

            await service.ForceRemoveManagedWorktreeAsync(creation.WorkspaceRoot);
            Directory.Exists(creation.WorkspaceRoot.RootPath).Should().BeFalse();
            (await store.GetCheckoutAsync(creation.WorkspaceRoot.Id)).Should().BeNull();
        }
        finally
        {
            SqliteTestPools.ClearFor(Path.Combine(testRoot, "appdata", "selfclaw.db"));
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task Force_remove_reclaims_a_worktree_whose_branch_name_no_longer_resolves()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testRoot, "repo");
        Directory.CreateDirectory(repositoryPath);
        var storagePaths = StoragePathDefaults.Create(
            Path.Combine(testRoot, "appdata"),
            Path.Combine(testRoot, "appdata", "selfclaw.db"),
            Path.Combine(testRoot, "appdata", "secrets"));

        try
        {
            await RunGitAsync(testRoot, "init", "-b", "main", repositoryPath);
            await RunGitAsync(repositoryPath, "config", "user.email", "tests@selfclaw.local");
            await RunGitAsync(repositoryPath, "config", "user.name", "SelfClaw Tests");
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "README.md"), "base\n");
            await RunGitAsync(repositoryPath, "add", "README.md");
            await RunGitAsync(repositoryPath, "commit", "-m", "initial");

            var database = new SelfClaw.Infrastructure.Data.Sqlite.SqliteDatabase(storagePaths);
            var conversations = new SqliteConversationRepository(database);
            await conversations.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var sourceWorkspace = new WorkspaceRoot(Guid.NewGuid(), "repo", repositoryPath, now, now);
            var store = new SqliteWorkspaceRepository(database);
            await store.UpsertWorkspaceRootAsync(sourceWorkspace);
            var service = new GitWorkspaceService(new GitCommandRunner(), store, store, storagePaths);
            await service.GetStateAsync(sourceWorkspace);

            var creation = await service.CreateManagedWorktreeAsync(sourceWorkspace, Guid.NewGuid(), "Stuck worktree");

            // Simulate the historical defect: the branch was renamed out from under the record,
            // so the recorded task branch no longer exists for the merge-base guard.
            await RunGitAsync(repositoryPath, "branch", "-m", creation.Checkout.BranchName, "selfclaw/orphaned");

            // The safe path is blocked, and the escape hatch still reclaims the directory.
            var safe = () => service.RemoveManagedWorktreeAsync(creation.WorkspaceRoot);
            await safe.Should().ThrowAsync<InvalidOperationException>();
            Directory.Exists(creation.WorkspaceRoot.RootPath).Should().BeTrue();

            await service.ForceRemoveManagedWorktreeAsync(creation.WorkspaceRoot);
            Directory.Exists(creation.WorkspaceRoot.RootPath).Should().BeFalse();
            (await store.GetCheckoutAsync(creation.WorkspaceRoot.Id)).Should().BeNull();
        }
        finally
        {
            SqliteTestPools.ClearFor(Path.Combine(testRoot, "appdata", "selfclaw.db"));
            DeleteTestDirectory(testRoot);
        }
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}{output}");
        }
    }

    private static void DeleteTestDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
