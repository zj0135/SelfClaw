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
            _ripgrepPath.Value, root, searchRoot, query, options, maxResults, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(
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

        var globMatcher = BuildGlobMatcher(pattern)
            ?? throw new ArgumentException("A glob pattern is required.", nameof(pattern));

        // Reuse the searchable-file walk (skips build/dependency/hidden dirs),
        // match against the workspace-relative forward-slash path, and order
        // by most-recently-modified so the freshest matches surface first —
        // the ordering mainstream Glob tools use.
        var matches = new List<(WorkspaceFileEntry Entry, DateTime Modified)>();
        foreach (var path in WorkspaceFileAccess.EnumerateSearchableFiles(searchRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeForward = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!globMatcher(relativeForward))
            {
                continue;
            }

            var info = new FileInfo(path);
            matches.Add((
                new WorkspaceFileEntry(Path.GetRelativePath(root, path), false, info.Length),
                info.LastWriteTimeUtc));
        }

        var entries = matches
            .OrderByDescending(match => match.Modified)
            .ThenBy(match => match.Entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListedEntries)
            .Select(match => match.Entry)
            .ToArray();

        return Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>(entries);
    }

    private async Task<IReadOnlyList<WorkspaceSearchHit>> SearchWithRipgrepAsync(
        string ripgrepPath,
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
        foreach (var skipped in WorkspaceFileAccess.SkippedDirectoryNames)
        {
            arguments.Add("--glob");
            arguments.Add($"!**/{skipped}/**");
        }

        arguments.Add("--");
        arguments.Add(query);
        arguments.Add(searchRoot);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ripgrepPath,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

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

    private static Func<string, bool>? BuildGlobMatcher(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
        {
            return null;
        }

        var normalized = glob.Replace('\\', '/');
        var builder = new StringBuilder("^");
        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];
            switch (current)
            {
                case '*':
                    if (index + 1 < normalized.Length && normalized[index + 1] == '*')
                    {
                        builder.Append(".*");
                        index++;
                        // Swallow a trailing slash after ** so "src/**/x" matches "src/x".
                        if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                        {
                            index++;
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(System.Text.RegularExpressions.Regex.Escape(current.ToString()));
                    break;
            }
        }

        builder.Append('$');
        var regex = new System.Text.RegularExpressions.Regex(
            builder.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return candidate => regex.IsMatch(candidate);
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
