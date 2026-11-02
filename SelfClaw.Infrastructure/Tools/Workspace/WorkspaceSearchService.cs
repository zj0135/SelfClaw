using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Infrastructure.Processes;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal sealed class WorkspaceSearchService
{
    private const int MaxListedEntries = 250;
    private const int MaxSearchHits = 80;
    private const int SearchTimeoutSeconds = 60;

    /// <summary>
    /// One match keeps at most this much of its line. Generated artifacts (minified bundles, single-line JSON)
    /// put an entire file on one line, and shipping that whole line costs more context than the rest of the
    /// turn while telling the model nothing it can act on. Ordinary source lines are far shorter and pass
    /// through untouched.
    /// </summary>
    internal const int MaximumLineCharacters = 1_000;

    /// <summary>Characters kept on each side of the match when a line has to be windowed.</summary>
    internal const int LineContextCharacters = 400;

    /// <summary>
    /// Total budget for one result. It stays well under <see cref="ToolResultTextLimits.MaximumCharacters"/>
    /// because the structured payload re-encodes the same text as JSON, where quotes and newlines expand.
    /// </summary>
    internal const int MaximumResultCharacters = 32 * 1024;

    private const string Ellipsis = "…";

    private readonly Lazy<string> _ripgrepPath = new(ResolveBundledRipgrep);

    public async Task<WorkspaceSearchResult> SearchTextAsync(
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
        var (hits, matchLimitReached) = await SearchWithRipgrepAsync(
            root, searchRoot, query, options, maxResults, cancellationToken).ConfigureAwait(false);
        return ApplySizeLimit(hits, matchLimitReached);
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
        using var registration = cancellationToken.Register(() => ProcessTree.TryKill(process));
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
            ProcessTree.TryKill(process);
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

    private async Task<(IReadOnlyList<WorkspaceSearchHit> Hits, bool MatchLimitReached)> SearchWithRipgrepAsync(
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
            "--max-filesize", $"{WorkspaceFileAccess.MaxTextFileBytes}",
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

        using var registration = cancellationToken.Register(() => ProcessTree.TryKill(process));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var (hits, matchLimitReached) = await ReadMatchesAsync(process, root, maxResults, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(SearchTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode > 1 && hits.Count < maxResults)
            {
                throw new InvalidOperationException($"Workspace search failed: {error.Trim()}");
            }

            return (hits, matchLimitReached);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The workspace search timed out after {SearchTimeoutSeconds} seconds.", exception);
        }
        finally
        {
            ProcessTree.TryKill(process);
        }
    }

    private static async Task<(IReadOnlyList<WorkspaceSearchHit> Hits, bool MatchLimitReached)> ReadMatchesAsync(
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
                // The cap stopped the scan, so matches may remain unseen; the caller reports that instead of
                // letting the hit count read as the repository total.
                ProcessTree.TryKill(process);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return (hits, true);
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (hits, false);
    }

    /// <summary>
    /// Stops adding matches once the next one would exceed the result budget. One hit is always kept, because
    /// a single windowed line fits by construction and returning nothing for a query that did match is a lie.
    /// </summary>
    private static WorkspaceSearchResult ApplySizeLimit(
        IReadOnlyList<WorkspaceSearchHit> hits, bool matchLimitReached)
    {
        var kept = new List<WorkspaceSearchHit>(hits.Count);
        var used = 0;
        foreach (var hit in hits)
        {
            var cost = hit.RelativePath.Length + hit.LineText.Length;
            if (kept.Count > 0 && used + cost > MaximumResultCharacters)
            {
                return new WorkspaceSearchResult(kept, matchLimitReached, SizeLimitReached: true);
            }

            kept.Add(hit);
            used += cost;
        }

        return new WorkspaceSearchResult(kept, matchLimitReached, SizeLimitReached: false);
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
            // Trim only after windowing: ripgrep's submatch offsets point into the untrimmed line.
            return new WorkspaceSearchHit(relativePath, lineNumber, WindowLine(lineText ?? string.Empty, data).Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps a short line whole and windows a long one around the match. Cutting the head off instead would
    /// hand the model a line that does not contain what it searched for and read as "no match here", while
    /// sending the line whole is what made one generated bundle cost a fifth of the context window. The
    /// markers state how many characters are missing on each side so the model can decide to page the file.
    /// </summary>
    private static string WindowLine(string line, JsonElement data)
    {
        if (line.Length <= MaximumLineCharacters)
        {
            return line;
        }

        var (matchStart, matchLength) = ReadFirstMatch(data, line);
        var length = Math.Min(matchLength, MaximumLineCharacters);
        var context = Math.Clamp((MaximumLineCharacters - length) / 2, 0, LineContextCharacters);
        var start = Math.Max(0, matchStart - context);
        var end = Math.Min(line.Length, matchStart + length + context);

        var window = new StringBuilder(end - start + 48);
        if (start > 0)
        {
            window.Append(Ellipsis).Append("[+").Append(start).Append(" chars] ");
        }

        window.Append(line, start, end - start);
        if (end < line.Length)
        {
            window.Append(' ').Append(Ellipsis).Append("[+").Append(line.Length - end).Append(" chars]");
        }

        return window.ToString();
    }

    /// <summary>
    /// The first submatch of the line: where the window starts and how much of it the window must keep. A
    /// missing submatch falls back to the head of the line, which is what a window without a match becomes.
    /// </summary>
    private static (int Start, int Length) ReadFirstMatch(JsonElement data, string line)
    {
        if (!data.TryGetProperty("submatches", out var submatches) ||
            submatches.ValueKind != JsonValueKind.Array || submatches.GetArrayLength() == 0)
        {
            return (0, 0);
        }

        var first = submatches[0];
        var start = first.TryGetProperty("start", out var startElement) && startElement.TryGetInt32(out var byteOffset)
            ? CharIndexAtByteOffset(line, byteOffset)
            : 0;
        var matchLength = first.TryGetProperty("match", out var matchElement)
            ? ReadRipgrepText(matchElement)?.Length ?? 0
            : 0;
        return (start, matchLength);
    }

    /// <summary>
    /// Converts a UTF-8 byte offset into the character index of the same position: ripgrep reports bytes, and a
    /// window built on those would cut multi-byte characters (CJK comments, emoji) in half.
    /// </summary>
    private static int CharIndexAtByteOffset(string text, int byteOffset)
    {
        if (byteOffset <= 0)
        {
            return 0;
        }

        var bytes = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (bytes >= byteOffset)
            {
                return index;
            }

            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length)
            {
                bytes += 4;
                index++;
                continue;
            }

            bytes += text[index] < 0x80 ? 1 : text[index] < 0x800 ? 2 : 3;
        }

        return text.Length;
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
                FileName = _ripgrepPath.Value,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
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
            $"The bundled ripgrep binary ('{executableName}') was not found. Probed '{candidate}' and '{beside}'. " +
            "Ship the 'runtimes\\win-x64\\native' folder next to the executable.");
    }
}
