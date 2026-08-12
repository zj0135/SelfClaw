using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Git;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Git;

public sealed class GitWorkspaceServiceTests
{
    [Fact]
    public async Task Managed_worktree_can_be_committed_merged_and_removed_safely()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testRoot, "repo");
        Directory.CreateDirectory(repositoryPath);
        var storagePaths = new StoragePaths(
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
            await conversations.UpsertWorkspaceRootAsync(sourceWorkspace);

            var store = new SqliteGitWorkspaceRepository(database);
            var runner = new GitCommandRunner();
            var service = new GitWorkspaceService(runner, store, conversations, storagePaths);
            var mergeService = new GitMergeService(runner, store, conversations, service);

            var sourceState = await service.GetStateAsync(sourceWorkspace);
            sourceState.IsRepository.Should().BeTrue();
            sourceState.BranchName.Should().Be("main");
            sourceState.IsDirty.Should().BeFalse();

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
            SqliteConnection.ClearAllPools();
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task Dirty_worktree_cannot_be_removed_before_merge()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testRoot, "repo");
        Directory.CreateDirectory(repositoryPath);
        var storagePaths = new StoragePaths(
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
            await conversations.UpsertWorkspaceRootAsync(sourceWorkspace);
            var store = new SqliteGitWorkspaceRepository(database);
            var service = new GitWorkspaceService(new GitCommandRunner(), store, conversations, storagePaths);
            await service.GetStateAsync(sourceWorkspace);

            var creation = await service.CreateManagedWorktreeAsync(sourceWorkspace, Guid.NewGuid(), "Dirty change");
            await File.AppendAllTextAsync(Path.Combine(creation.WorkspaceRoot.RootPath, "README.md"), "uncommitted\n");

            var act = () => service.RemoveManagedWorktreeAsync(creation.WorkspaceRoot);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*uncommitted*");
            Directory.Exists(creation.WorkspaceRoot.RootPath).Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
