using System.Text;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Infrastructure.Git;

internal sealed class GitDiffService : IGitDiffQuery
{
    private static readonly IReadOnlyDictionary<string, (int? Added, int? Removed)> EmptyCounts =
        new Dictionary<string, (int? Added, int? Removed)>();

    private readonly GitCommandRunner _runner;

    public GitDiffService(GitCommandRunner runner)
    {
        _runner = runner;
    }

    public async Task<GitWorkspaceChanges> GetChangesAsync(
        WorkspaceRoot workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        if (!Directory.Exists(workspaceRoot.RootPath))
        {
            return CreateNotRepository("Workspace directory does not exist.");
        }

        GitCommandResult probe;
        try
        {
            probe = await RunGitAsync(
                workspaceRoot.RootPath,
                ["rev-parse", "--show-toplevel", "--show-prefix"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return CreateNotRepository(exception.Message, isGitAvailable: false);
        }

        if (!probe.Succeeded)
        {
            return CreateNotRepository(probe.Message);
        }

        var probeLines = probe.StandardOutput.Split('\n');
        if (string.IsNullOrWhiteSpace(probeLines[0]))
        {
            return CreateNotRepository("Git did not report a repository root.");
        }

        return await ReadChangesAsync(
            workspaceRoot.RootPath,
            Path.GetFullPath(probeLines[0].Trim()),
            probeLines.Length > 1 ? probeLines[1].Trim() : string.Empty,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitFileDiff> GetFileDiffAsync(
        WorkspaceRoot workspaceRoot,
        GitChangeScope scope,
        string path,
        string? oldPath,
        GitChangeKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        var relativePath = NormalizeRelativePath(path);
        var relativeOldPath = string.IsNullOrWhiteSpace(oldPath) ? null : NormalizeRelativePath(oldPath);

        if (kind == GitChangeKind.Untracked && scope == GitChangeScope.Unstaged)
        {
            return await SynthesizeUntrackedDiffAsync(workspaceRoot.RootPath, relativePath, cancellationToken).ConfigureAwait(false);
        }

        var arguments = new List<string>
        {
            "-c", "core.quotepath=false",
            "diff", "--no-color", "--no-ext-diff", "--find-renames", "--unified=3", "--relative"
        };
        if (scope == GitChangeScope.Staged)
        {
            arguments.Add("--cached");
        }

        arguments.Add("--");
        arguments.Add(relativePath);
        if (relativeOldPath is not null && !string.Equals(relativeOldPath, relativePath, StringComparison.Ordinal))
        {
            arguments.Add(relativeOldPath);
        }

        var result = await RunGitAsync(workspaceRoot.RootPath, arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && result.ExitCode != 1)
        {
            throw new InvalidOperationException(result.Message);
        }

        var patch = result.StandardOutput;
        var isBinary = patch.Contains("Binary files ", StringComparison.Ordinal) ||
                       patch.Contains("GIT binary patch", StringComparison.Ordinal);
        var (added, removed) = CountPatchLines(patch);
        var truncated = false;
        if (patch.Length > GitDiffLimits.MaximumPatchCharacters)
        {
            patch = TruncatePatch(patch);
            truncated = true;
        }

        return new GitFileDiff(relativePath, relativeOldPath, kind, isBinary, patch, truncated, added, removed);
    }

    // HEAD may be unborn in a fresh repository: symbolic-ref failing only means there is no branch
    // name yet, while status and diffs still describe the changes.
    private async Task<GitWorkspaceChanges> ReadChangesAsync(
        string workingDirectory,
        string repositoryRootPath,
        string prefix,
        CancellationToken cancellationToken)
    {
        var branchTask = RunGitAsync(workingDirectory, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        var statusTask = RunGitAsync(
            workingDirectory,
            ["-c", "core.quotepath=false", "status", "--porcelain=v2", "-z", "--untracked-files=all", "--", "."],
            cancellationToken);
        var unstagedCountsTask = RunGitAsync(
            workingDirectory,
            ["-c", "core.quotepath=false", "diff", "--numstat", "-z", "--relative", "--", "."],
            cancellationToken);
        var stagedCountsTask = RunGitAsync(
            workingDirectory,
            ["-c", "core.quotepath=false", "diff", "--numstat", "-z", "--cached", "--relative", "--", "."],
            cancellationToken);
        await Task.WhenAll(branchTask, statusTask, unstagedCountsTask, stagedCountsTask).ConfigureAwait(false);

        var status = await statusTask.ConfigureAwait(false);
        var branch = await branchTask.ConfigureAwait(false);
        var entries = status.Succeeded ? GitStatusPorcelainParser.ParseStatus(status.StandardOutput, prefix) : [];
        return new GitWorkspaceChanges(
            true,
            true,
            null,
            repositoryRootPath,
            branch.Succeeded && !string.IsNullOrWhiteSpace(branch.StandardOutput) ? branch.StandardOutput.Trim() : null,
            BuildChangeSet(entries, GitChangeScope.Unstaged, ParseCounts(await unstagedCountsTask.ConfigureAwait(false))),
            BuildChangeSet(entries, GitChangeScope.Staged, ParseCounts(await stagedCountsTask.ConfigureAwait(false))));
    }

    // Untracked files have no diff, so their whole content becomes one added hunk. Synthesizing here
    // instead of `git diff --no-index` keeps exit-code and /dev/null platform quirks out of the path.
    private static async Task<GitFileDiff> SynthesizeUntrackedDiffAsync(
        string rootPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var fullPath = WorkspaceFileAccess.ResolvePath(rootPath, relativePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("文件已被删除或移动。", relativePath);
        }

        var truncated = file.Length > WorkspaceFileAccess.MaxTextFileBytes;
        var bytes = truncated
            ? await WorkspaceFileAccess.ReadHeaderAsync(fullPath, (int)WorkspaceFileAccess.MaxTextFileBytes, cancellationToken).ConfigureAwait(false)
            : await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan().Contains((byte)0))
        {
            return new GitFileDiff(relativePath, null, GitChangeKind.Untracked, true, null, false, 0, 0);
        }

        var lines = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
        var lineCount = lines.Length;
        if (lineCount > 0 && lines[^1].Length == 0)
        {
            // A trailing newline yields one empty trailing element; it is not a content line.
            lineCount--;
        }

        var builder = new StringBuilder();
        builder.Append("diff --git a/").Append(relativePath).Append(" b/").Append(relativePath).Append('\n');
        builder.Append("new file mode 100644\n");
        builder.Append("--- /dev/null\n");
        builder.Append("+++ b/").Append(relativePath).Append('\n');
        builder.Append("@@ -0,0 +1,").Append(lineCount).Append(" @@\n");
        for (var index = 0; index < lineCount; index++)
        {
            builder.Append('+').Append(lines[index].TrimEnd('\r')).Append('\n');
        }

        var patch = builder.ToString();
        if (patch.Length > GitDiffLimits.MaximumPatchCharacters)
        {
            patch = TruncatePatch(patch);
            truncated = true;
        }

        return new GitFileDiff(relativePath, null, GitChangeKind.Untracked, false, patch, truncated, lineCount, 0);
    }

    private static GitChangeSet BuildChangeSet(
        IReadOnlyList<GitStatusPorcelainParser.GitStatusEntry> entries,
        GitChangeScope scope,
        IReadOnlyDictionary<string, (int? Added, int? Removed)> counts)
    {
        var files = entries
            .Where(entry => entry.Scope == scope)
            .Select(entry => CreateChangedFile(entry, counts))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        var atFileLimit = files.Length > GitDiffLimits.MaximumFiles;
        if (atFileLimit)
        {
            files = files[..GitDiffLimits.MaximumFiles];
        }

        return new GitChangeSet(files, atFileLimit);
    }

    private static GitChangedFile CreateChangedFile(
        GitStatusPorcelainParser.GitStatusEntry entry,
        IReadOnlyDictionary<string, (int? Added, int? Removed)> counts)
    {
        var found = counts.TryGetValue(entry.Path, out var lineCounts);
        var isBinary = found &&
                       entry.Kind is not (GitChangeKind.Untracked or GitChangeKind.Conflicted) &&
                       lineCounts.Added is null && lineCounts.Removed is null;
        return new GitChangedFile(entry.Path, entry.OldPath, entry.Kind, lineCounts.Added, lineCounts.Removed, isBinary);
    }

    private static IReadOnlyDictionary<string, (int? Added, int? Removed)> ParseCounts(GitCommandResult result)
        => result.Succeeded ? GitStatusPorcelainParser.ParseNumstat(result.StandardOutput) : EmptyCounts;

    private static (int Added, int Removed) CountPatchLines(string patch)
    {
        var added = 0;
        var removed = 0;
        var position = 0;
        while (position < patch.Length)
        {
            var end = patch.IndexOf('\n', position);
            end = end < 0 ? patch.Length : end;
            var line = patch.AsSpan(position, end - position);
            if (!line.IsEmpty && line[0] == '+' && !line.StartsWith("+++"))
            {
                added++;
            }
            else if (!line.IsEmpty && line[0] == '-' && !line.StartsWith("---"))
            {
                removed++;
            }

            position = end + 1;
        }

        return (added, removed);
    }

    private static string TruncatePatch(string patch)
    {
        var cut = patch.LastIndexOf('\n', GitDiffLimits.MaximumPatchCharacters - 1);
        return cut >= 0 ? patch[..(cut + 1)] : patch[..GitDiffLimits.MaximumPatchCharacters];
    }

    private static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new ArgumentException($"'{path}' is not a workspace-relative path.", nameof(path));
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException($"'{path}' is not a workspace-relative path.", nameof(path));
            }
        }

        return normalized;
    }

    private static GitWorkspaceChanges CreateNotRepository(string? error, bool isGitAvailable = true)
        => new(
            isGitAvailable,
            false,
            error,
            null,
            null,
            new GitChangeSet([], false),
            new GitChangeSet([], false));

    private Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => _runner.RunAsync(workingDirectory, arguments, cancellationToken);
}
