using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal sealed class WorkspaceSearchService
{
    private const int MaxListedEntries = 250;
    private const int MaxSearchHits = 80;
    private const int SearchTimeoutSeconds = 60;
    private readonly Lazy<string> _ripgrepPath = new(ResolveBundledRipgrep);

    public async Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
        string workspaceRootPath,
        string query,
        WorkspaceSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query must not be empty.", nameof(query));
        }

        var root = WorkspaceFileAccess.NormalizeRoot(workspaceRootPath);
        options ??= new WorkspaceSearchOptions();
        var maxResults = options.MaxResults is int requested and > 0
            ? Math.Min(requested, MaxSearchHits)
            : MaxSearchHits;

        // Scope the search to a sub-directory when requested. Resolving through
        // WorkspaceFileAccess.ResolvePath keeps the traversal guard intact.
        var searchRoot = string.IsNullOrWhiteSpace(options.RelativePath)
            ? root
            : WorkspaceFileAccess.ResolvePath(root, options.RelativePath);
        if (!Directory.Exists(searchRoot))
        {
            throw new DirectoryNotFoundException($"Directory '{options.RelativePath}' was not found.");
        }

        // Ripgrep is bundled with the app: multi-threaded, honours .gitignore,
        // and beats a hand-rolled managed scan by 1-2 orders of magnitude.
        return await SearchWithRipgrepAsync(
            root, searchRoot, query, options, maxResults, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(
        string workspaceRootPath,
        string pattern,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("A glob pattern is required.", nameof(pattern));
        }

        var root = WorkspaceFileAccess.NormalizeRoot(workspaceRootPath);
        var searchRoot = string.IsNullOrWhiteSpace(relativePath)
            ? root
            : WorkspaceFileAccess.ResolvePath(root, relativePath);
        if (!Directory.Exists(searchRoot))
        {
            throw new DirectoryNotFoundException($"Directory '{relativePath}' was not found.");
        }

        // Glob lists paths independently of .gitignore, including hidden files, but not hidden
        // or build/dependency directories. Patterns remain anchored to the workspace root.
        var arguments = new List<string>
        {
            "--files", "--hidden", "--no-ignore", "--no-messages",
            "--iglob", "/" + pattern.Replace('\\', '/').TrimStart('/'),
            "--iglob", "!**/.*/**"
        };
        AddDirectoryExclusions(arguments, "--iglob");
        arguments.AddRange(["--", searchRoot]);
        using var process = CreateProcess(root, arguments);
        if (!process.Start()) throw new InvalidOperationException("Failed to start ripgrep.");
        using var registration = cancellationToken.Register(() => WorkspaceProcess.TryKill(process));
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var entries = await ReadGlobMatchesAsync(process, root, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(SearchTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            var error = await errors.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode > 1) throw new InvalidOperationException($"Workspace glob failed: {error.Trim()}");
            return entries;
        }
        finally
        {
            WorkspaceProcess.TryKill(process);
        }
    }

    private static async Task<IReadOnlyList<WorkspaceFileEntry>> ReadGlobMatchesAsync(Process process, string root, CancellationToken cancellationToken)
    {
        var comparer = Comparer<(DateTime Modified, string Path)>.Create((left, right) =>
        {
            var byTime = left.Modified.CompareTo(right.Modified);
            return byTime != 0 ? byTime : StringComparer.OrdinalIgnoreCase.Compare(right.Path, left.Path);
        });
        var latest = new PriorityQueue<WorkspaceFileEntry, (DateTime Modified, string Path)>(comparer);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } path)
        {
            var fullPath = WorkspaceFileAccess.ResolvePath(root, path);
            if (HasHiddenDirectory(root, fullPath)) continue;
            var info = new FileInfo(fullPath);
            if (!info.Exists) continue;
            var relative = Path.GetRelativePath(root, fullPath);
            var priority = (info.LastWriteTimeUtc, relative);
            if (latest.Count == MaxListedEntries && latest.TryPeek(out _, out var oldest) && comparer.Compare(priority, oldest) <= 0) continue;
            latest.Enqueue(new WorkspaceFileEntry(relative, false, info.Length), priority);
            if (latest.Count > MaxListedEntries) latest.Dequeue();
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return latest.UnorderedItems.OrderByDescending(item => item.Priority.Modified)
            .ThenBy(item => item.Element.RelativePath, StringComparer.OrdinalIgnoreCase).Select(item => item.Element).ToArray();
    }

    private static bool HasHiddenDirectory(string root, string path)
    {
        for (var directory = Path.GetDirectoryName(path); directory is not null &&
             !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase); directory = Path.GetDirectoryName(directory))
        {
            try
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.Hidden)) return true;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        return false;
    }

    private async Task<IReadOnlyList<WorkspaceSearchHit>> SearchWithRipgrepAsync(
        string root,
        string searchRoot,
        string query,
        WorkspaceSearchOptions options,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--json",
            "--line-number",
            "--max-count", maxResults.ToString(),
            "--max-filesize", $"{WorkspaceFileAccess.MaxFileBytes}",
            "--threads", "0"
        };

        if (!options.CaseSensitive)
        {
            arguments.Add("--ignore-case");
        }

        if (!options.IsRegex)
        {
            arguments.Add("--fixed-strings");
        }

        if (!string.IsNullOrWhiteSpace(options.Glob))
        {
            arguments.Add("--glob");
            arguments.Add(options.Glob);
        }

        // Always exclude the build/dependency directories the managed scan skips, in
        // case they are not covered by a .gitignore.
        AddDirectoryExclusions(arguments, "--glob");

        arguments.Add("--");
        arguments.Add(query);
        arguments.Add(searchRoot);

        using var process = CreateProcess(root, arguments);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ripgrep.");
        }

        using var registration = cancellationToken.Register(() => WorkspaceProcess.TryKill(process));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var hits = await ReadMatchesAsync(process, root, maxResults, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(SearchTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode > 1 && hits.Count < maxResults)
            {
                throw new InvalidOperationException($"Workspace search failed: {error.Trim()}");
            }

            return hits;
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The workspace search timed out after {SearchTimeoutSeconds} seconds.", exception);
        }
        finally
        {
            WorkspaceProcess.TryKill(process);
        }
    }

    private static async Task<IReadOnlyList<WorkspaceSearchHit>> ReadMatchesAsync(
        Process process, string root, int maxResults, CancellationToken cancellationToken)
    {
        var hits = new List<WorkspaceSearchHit>(maxResults);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } jsonLine)
        {
            if (jsonLine.Length == 0) continue;
            var hit = ParseRipgrepMatch(jsonLine, root);
            if (hit is null) continue;
            hits.Add(hit);
            if (hits.Count >= maxResults)
            {
                WorkspaceProcess.TryKill(process);
                break;
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return hits;
    }

    private static WorkspaceSearchHit? ParseRipgrepMatch(string jsonLine, string root)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var element = document.RootElement;
            if (!element.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "match")
            {
                return null;
            }

            var data = element.GetProperty("data");
            var path = ReadRipgrepText(data.GetProperty("path"));
            var lineText = ReadRipgrepText(data.GetProperty("lines"));
            var lineNumber = data.GetProperty("line_number").GetInt32();

            if (path is null)
            {
                return null;
            }

            var relativePath = Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path;
            return new WorkspaceSearchHit(relativePath, lineNumber, (lineText ?? string.Empty).Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadRipgrepText(JsonElement element)
    {
        if (element.TryGetProperty("text", out var text))
        {
            return text.GetString();
        }

        if (element.TryGetProperty("bytes", out var bytes) && bytes.GetString() is { } encoded)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    private Process CreateProcess(string root, IEnumerable<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ripgrepPath.Value, WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        return process;
    }

    private static void AddDirectoryExclusions(List<string> arguments, string globOption)
    {
        foreach (var skipped in WorkspaceFileAccess.SkippedDirectoryNames)
        {
            arguments.Add(globOption);
            arguments.Add($"!**/{skipped}/**");
        }
    }

    private static string ResolveBundledRipgrep()
    {
        var executableName = "rg.exe";
        var baseDirectory = AppContext.BaseDirectory;

        var candidate = Path.Combine(baseDirectory, "runtimes", "win-x64", "native", executableName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var beside = Path.Combine(baseDirectory, executableName);
        if (File.Exists(beside))
        {
            return beside;
        }

        throw new FileNotFoundException(
            $"The bundled ripgrep binary ('{executableName}') was not found under '{baseDirectory}runtimes/<rid>/native'. " +
            "Ensure it ships with the application.");
    }
}
