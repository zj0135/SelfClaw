using System.Diagnostics;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Git;

namespace SelfClaw.Tests.Infrastructure.Git;

public sealed class GitDiffServiceTests
{
    [Fact]
    public async Task Lists_unstaged_and_staged_changes_with_line_counts()
    {
        using var repository = await TestRepository.CreateAsync();
        await File.WriteAllTextAsync(repository.FilePath("a.txt"), "one\ntwo\nthree\n");
        await repository.GitAsync("add", "a.txt");
        await File.WriteAllTextAsync(repository.FilePath("a.txt"), "one\ntwo\nthree\nfour\n");
        await File.WriteAllTextAsync(repository.FilePath("fresh.txt"), "fresh line\n");
        await File.WriteAllBytesAsync(repository.FilePath("blob.dat"), new byte[] { 0x62, 0x69, 0x6e, 0x00, 0x64 });

        var changes = await repository.Service.GetChangesAsync(repository.Root);

        changes.IsGitAvailable.Should().BeTrue();
        changes.IsRepository.Should().BeTrue();
        changes.BranchName.Should().Be("main");
        changes.RepositoryRootPath.Should().BeEquivalentTo(repository.RepositoryPath);
        changes.Unstaged.AtFileLimit.Should().BeFalse();
        changes.Unstaged.Files.Select(file => (file.Path, file.Kind)).Should().Equal(
            ("a.txt", GitChangeKind.Modified),
            ("blob.dat", GitChangeKind.Untracked),
            ("fresh.txt", GitChangeKind.Untracked));
        changes.Unstaged.Files[0].AddedLines.Should().Be(1);
        changes.Unstaged.Files[0].RemovedLines.Should().Be(0);
        changes.Unstaged.Files[1].AddedLines.Should().BeNull();
        changes.Staged.Files.Select(file => (file.Path, file.Kind)).Should().Equal(("a.txt", GitChangeKind.Modified));
        changes.Staged.Files[0].AddedLines.Should().Be(1);
    }

    [Fact]
    public async Task Staged_rename_carries_the_old_path_and_its_patch_names_both_sides()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.GitAsync("mv", "a.txt", "renamed.txt");

        var changes = await repository.Service.GetChangesAsync(repository.Root);

        var file = changes.Staged.Files.Should().ContainSingle().Subject;
        file.Should().BeEquivalentTo(new GitChangedFile("renamed.txt", "a.txt", GitChangeKind.Renamed, 0, 0, false));
        changes.Unstaged.Files.Should().BeEmpty();

        var diff = await repository.Service.GetFileDiffAsync(repository.Root, GitChangeScope.Staged, file.Path, file.OldPath, file.Kind);

        diff.IsBinary.Should().BeFalse();
        diff.Patch.Should().Contain("rename from a.txt").And.Contain("rename to renamed.txt");
    }

    [Fact]
    public async Task Workspace_root_inside_a_subdirectory_only_sees_that_subtree_with_relative_paths()
    {
        using var repository = await TestRepository.CreateAsync();
        await File.WriteAllTextAsync(repository.FilePath("a.txt"), "changed outside\n");
        await File.WriteAllTextAsync(repository.FilePath("dir/inside.txt"), "inside\nmore\n");
        await repository.GitAsync("add", "dir/inside.txt");
        await File.WriteAllTextAsync(repository.FilePath("dir/fresh.txt"), "x\n");
        var subtree = repository.RootFor("dir");

        var changes = await repository.Service.GetChangesAsync(subtree);

        changes.Staged.Files.Select(file => file.Path).Should().Equal("inside.txt");
        changes.Staged.Files[0].AddedLines.Should().Be(1);
        changes.Unstaged.Files.Select(file => file.Path).Should().Equal("fresh.txt");

        var diff = await repository.Service.GetFileDiffAsync(subtree, GitChangeScope.Staged, "inside.txt", null, GitChangeKind.Modified);

        diff.Patch.Should().Contain("+++ b/inside.txt").And.Contain("+more");
        diff.AddedLines.Should().Be(1);
    }

    [Fact]
    public async Task Untracked_files_are_synthesized_as_added_hunks_and_binaries_are_flagged()
    {
        using var repository = await TestRepository.CreateAsync();
        await File.WriteAllTextAsync(repository.FilePath("fresh.txt"), "first\r\nsecond\n");
        await File.WriteAllBytesAsync(repository.FilePath("blob.dat"), new byte[] { 0x62, 0x00, 0x64 });

        var text = await repository.Service.GetFileDiffAsync(repository.Root, GitChangeScope.Unstaged, "fresh.txt", null, GitChangeKind.Untracked);

        text.IsBinary.Should().BeFalse();
        text.Truncated.Should().BeFalse();
        text.AddedLines.Should().Be(2);
        text.Patch.Should().Be(
            "diff --git a/fresh.txt b/fresh.txt\nnew file mode 100644\n--- /dev/null\n+++ b/fresh.txt\n@@ -0,0 +1,2 @@\n+first\n+second\n");

        var binary = await repository.Service.GetFileDiffAsync(repository.Root, GitChangeScope.Unstaged, "blob.dat", null, GitChangeKind.Untracked);

        binary.IsBinary.Should().BeTrue();
        binary.Patch.Should().BeNull();
    }

    [Fact]
    public async Task Oversized_patches_are_truncated_while_counts_stay_complete()
    {
        using var repository = await TestRepository.CreateAsync();
        var line = new string('x', 100) + "\n";
        await File.WriteAllTextAsync(repository.FilePath("a.txt"), string.Concat(Enumerable.Repeat(line, 6000)));

        var diff = await repository.Service.GetFileDiffAsync(repository.Root, GitChangeScope.Unstaged, "a.txt", null, GitChangeKind.Modified);

        diff.Truncated.Should().BeTrue();
        var patch = diff.Patch ?? string.Empty;
        patch.Length.Should().BeLessThanOrEqualTo(GitDiffLimits.MaximumPatchCharacters);
        patch.Should().EndWith("\n");
        diff.AddedLines.Should().Be(6000);
        diff.RemovedLines.Should().Be(2);
    }

    [Fact]
    public async Task Fresh_repository_without_commits_still_lists_staged_files()
    {
        using var repository = await TestRepository.CreateAsync(commit: false);
        await File.WriteAllTextAsync(repository.FilePath("s.txt"), "x\n");
        await repository.GitAsync("add", "s.txt");

        var changes = await repository.Service.GetChangesAsync(repository.Root);

        changes.IsRepository.Should().BeTrue();
        changes.BranchName.Should().Be("main");
        changes.Staged.Files.Select(file => (file.Path, file.Kind)).Should().Equal(("s.txt", GitChangeKind.Added));
        changes.Staged.Files[0].AddedLines.Should().Be(1);

        var diff = await repository.Service.GetFileDiffAsync(repository.Root, GitChangeScope.Staged, "s.txt", null, GitChangeKind.Added);

        diff.Patch.Should().Contain("+x");
    }

    [Fact]
    public async Task Directories_outside_git_report_not_a_repository()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var changes = await new GitDiffService(new GitCommandRunner())
                .GetChangesAsync(new WorkspaceRoot(Guid.NewGuid(), "plain", directory, now, now));

            changes.IsGitAvailable.Should().BeTrue();
            changes.IsRepository.Should().BeFalse();
            changes.Error.Should().NotBeNullOrWhiteSpace();
            changes.Unstaged.Files.Should().BeEmpty();
            changes.Staged.Files.Should().BeEmpty();
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("dir/../a.txt")]
    [InlineData("/a.txt")]
    [InlineData("C:\\a.txt")]
    [InlineData("./a.txt")]
    public async Task Paths_that_are_not_workspace_relative_are_rejected_before_git_runs(string path)
    {
        using var repository = await TestRepository.CreateAsync();

        var act = () => repository.Service.GetFileDiffAsync(repository.Root, GitChangeScope.Unstaged, path, null, GitChangeKind.Modified);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class TestRepository : IDisposable
    {
        private readonly string _testRoot;

        private TestRepository(string testRoot, string repositoryPath)
        {
            _testRoot = testRoot;
            RepositoryPath = repositoryPath;
            var now = DateTimeOffset.UtcNow;
            Root = new WorkspaceRoot(Guid.NewGuid(), "repo", repositoryPath, now, now);
        }

        public string RepositoryPath { get; }

        public WorkspaceRoot Root { get; }

        public GitDiffService Service { get; } = new(new GitCommandRunner());

        public static async Task<TestRepository> CreateAsync(bool commit = true)
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
            var repositoryPath = Path.Combine(testRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repositoryPath, "dir"));
            await RunGitAsync(testRoot, "init", "-q", "-b", "main", repositoryPath);
            await RunGitAsync(repositoryPath, "config", "user.email", "tests@selfclaw.local");
            await RunGitAsync(repositoryPath, "config", "user.name", "SelfClaw Tests");
            await RunGitAsync(repositoryPath, "config", "core.autocrlf", "false");
            if (commit)
            {
                await File.WriteAllTextAsync(Path.Combine(repositoryPath, "a.txt"), "one\ntwo\n");
                await File.WriteAllTextAsync(Path.Combine(repositoryPath, "dir", "inside.txt"), "inside\n");
                await RunGitAsync(repositoryPath, "add", "-A");
                await RunGitAsync(repositoryPath, "commit", "-q", "-m", "initial");
            }

            return new TestRepository(testRoot, repositoryPath);
        }

        public string FilePath(string relativePath)
            => Path.Combine(RepositoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public WorkspaceRoot RootFor(string relativeDirectory)
        {
            var now = DateTimeOffset.UtcNow;
            return new WorkspaceRoot(Guid.NewGuid(), relativeDirectory, Path.Combine(RepositoryPath, relativeDirectory), now, now);
        }

        public Task GitAsync(params string[] arguments) => RunGitAsync(RepositoryPath, arguments);

        public void Dispose() => DeleteTestDirectory(_testRoot);
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
