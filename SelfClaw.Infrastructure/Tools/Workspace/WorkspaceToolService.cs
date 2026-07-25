using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

public sealed class WorkspaceToolService : IWorkspaceToolService
{
    private const int MaxListedEntries = 250;
    private const int MaxSearchHits = 80;
    private const int MaxReadCharacters = 24_000;
    private const int MaxReadLines = 2_000;
    private const int MaxWriteCharacters = 200_000;
    private const int MaxShellOutputCharacters = 24_000;
    private const int MinShellTimeoutSeconds = 1;
    private const int MaxShellTimeoutSeconds = 600;
    private const int SearchTimeoutSeconds = 60;
    private const long MaxFileBytes = 1_000_000;
    private readonly ILogger<WorkspaceToolService> _logger;

    /// <summary>
    /// Absolute path to the bundled ripgrep executable, resolved once (the service is a
    /// singleton) and reused for every search.
    /// </summary>
    private readonly Lazy<string> _ripgrepPath = new(ResolveBundledRipgrep);

    /// <summary>
    /// Directory names to skip during recursive search (case-insensitive).
    /// Dot-prefixed and Hidden-attributed directories are already skipped by
    /// EnumerateSearchableFiles, so only non-dot build/dependency dirs are listed here.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "out", "build", "dist",
        "node_modules", "packages",
        "__pycache__",
        "target", "vendor"
    };

    public WorkspaceToolService(ILogger<WorkspaceToolService>? logger = null)
    {
        _logger = logger ?? NullLogger<WorkspaceToolService>.Instance;
    }

    public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
        string workspaceRootPath,
        string? relativePath,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            "list workspace files",
            workspaceRootPath,
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = NormalizeRoot(workspaceRootPath);
                var target = ResolvePath(root, relativePath ?? string.Empty);

                if (!Directory.Exists(target))
                {
                    throw new DirectoryNotFoundException($"Directory '{relativePath}' was not found.");
                }

                // A single EnumerateFileSystemInfos pass reuses the metadata the OS
                // already returned (attributes + length), avoiding a second stat per
                // entry. Noise directories (bin/obj/node_modules/hidden) are filtered
                // so large repos don't drown the listing.
                var entries = new DirectoryInfo(target)
                    .EnumerateFileSystemInfos()
                    .Where(info => !IsIgnoredListingEntry(info))
                    .Select(info =>
                    {
                        var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
                        long? size = isDirectory ? null : ((FileInfo)info).Length;
                        return new WorkspaceFileEntry(Path.GetRelativePath(root, info.FullName), isDirectory, size);
                    })
                    .OrderByDescending(entry => entry.IsDirectory)
                    .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxListedEntries)
                    .ToArray();

                return Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>(entries);
            },
            ("RelativePath", relativePath ?? string.Empty));

    public async Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
        string workspaceRootPath,
        string query,
        WorkspaceSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "search workspace text",
            workspaceRootPath,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    throw new ArgumentException("Query must not be empty.", nameof(query));
                }

                var root = NormalizeRoot(workspaceRootPath);
                options ??= new WorkspaceSearchOptions();
                var maxResults = options.MaxResults is int requested and > 0
                    ? Math.Min(requested, MaxSearchHits)
                    : MaxSearchHits;

                // Scope the search to a sub-directory when requested. Resolving through
                // ResolvePath keeps the traversal guard intact.
                var searchRoot = string.IsNullOrWhiteSpace(options.RelativePath)
                    ? root
                    : ResolvePath(root, options.RelativePath);
                if (!Directory.Exists(searchRoot))
                {
                    throw new DirectoryNotFoundException($"Directory '{options.RelativePath}' was not found.");
                }

                // Ripgrep is bundled with the app: multi-threaded, honours .gitignore,
                // and beats a hand-rolled managed scan by 1-2 orders of magnitude.
                return await SearchWithRipgrepAsync(
                    _ripgrepPath.Value, root, searchRoot, query, options, maxResults, cancellationToken);
            },
            ("QueryLength", query?.Length ?? 0),
            ("Glob", options?.Glob ?? string.Empty),
            ("IsRegex", options?.IsRegex ?? false));
    }

    /// <summary>
    /// Runs ripgrep in JSON mode and parses match events into hits. Ripgrep enforces
    /// binary detection, .gitignore filtering, and threading for us.
    /// </summary>
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
            "--max-filesize", $"{MaxFileBytes}",
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
        foreach (var skipped in SkippedDirectoryNames)
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

        using var registration = cancellationToken.Register(() => TryKillProcess(process));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(SearchTimeoutSeconds));

        var hits = new List<WorkspaceSearchHit>(maxResults);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeoutSource.Token) is { } jsonLine)
            {
                if (jsonLine.Length == 0)
                {
                    continue;
                }

                var hit = ParseRipgrepMatch(jsonLine, root);
                if (hit is not null)
                {
                    hits.Add(hit);
                    if (hits.Count >= maxResults)
                    {
                        TryKillProcess(process);
                        break;
                    }
                }
            }

            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw new TimeoutException($"The workspace search timed out after {SearchTimeoutSeconds} seconds.");
        }

        return hits;
    }

    /// <summary>
    /// Parses a single ripgrep <c>--json</c> line. Only <c>match</c> events yield a hit;
    /// begin/end/summary/context events are ignored.
    /// </summary>
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

    /// <summary>
    /// Ripgrep encodes text either as a UTF-8 string (<c>text</c>) or, for lossy bytes,
    /// as base64 (<c>bytes</c>). Handle both shapes.
    /// </summary>
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

    /// <summary>
    /// Translates a simple glob (<c>*</c>, <c>?</c>, <c>**</c>) into a matcher over
    /// forward-slash relative paths. Returns null when no glob is supplied.
    /// </summary>
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

    /// <summary>
    /// Resolves the absolute path to the ripgrep binary bundled with the application
    /// under <c>runtimes/&lt;rid&gt;/native</c>. Runs once (the service is a singleton)
    /// and the path is reused for every search. The binary is shipped with the app, so
    /// it is expected to exist; the probe simply locates it across the known rids.
    /// </summary>
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

    /// <summary>
    /// Directories excluded from <see cref="ListFilesAsync"/> so large repos don't
    /// surface build/dependency noise. Hidden and dot-prefixed entries are dropped too.
    /// </summary>
    private static bool IsIgnoredListingEntry(FileSystemInfo info)
    {
        if (info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            return true;
        }

        var name = info.Name;
        if (name.StartsWith('.'))
        {
            return true;
        }

        return info.Attributes.HasFlag(FileAttributes.Directory) && SkippedDirectoryNames.Contains(name);
    }

    public async Task<WorkspaceFileContent> ReadFileAsync(
        string workspaceRootPath,
        string relativePath,
        int? startLine = null,
        int? lineCount = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "read workspace file",
            workspaceRootPath,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    throw new ArgumentException("A file path is required.", nameof(relativePath));
                }

                var root = NormalizeRoot(workspaceRootPath);
                var fullPath = ResolvePath(root, relativePath);
                var fileInfo = new FileInfo(fullPath);

                if (!fileInfo.Exists)
                {
                    throw new FileNotFoundException("File was not found.", relativePath);
                }

                if (fileInfo.Length > MaxFileBytes)
                {
                    throw new InvalidOperationException($"The file is too large to read safely. Limit: {MaxFileBytes} bytes.");
                }

                if (!await IsTextFileAsync(fullPath, cancellationToken))
                {
                    throw new InvalidOperationException("Only text files can be read.");
                }

                var relative = Path.GetRelativePath(root, fullPath);

                // Line-ranged read: stream the file so a large file can be paged
                // without loading it all. startLine is 1-based; lineCount defaults
                // to MaxReadLines from the start line.
                if (startLine is not null || lineCount is not null)
                {
                    return await ReadLineRangeAsync(fullPath, relative, startLine, lineCount, cancellationToken);
                }

                // Whole-file read (legacy behaviour) with a character-count safety cap.
                var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
                var totalLines = CountLines(content);
                var truncated = content.Length > MaxReadCharacters;
                if (truncated)
                {
                    content = content[..MaxReadCharacters];
                }

                var endLine = truncated ? CountLines(content) : totalLines;
                return new WorkspaceFileContent(relative, content, truncated, 1, endLine, totalLines);
            },
            ("RelativePath", relativePath),
            ("StartLine", startLine),
            ("LineCount", lineCount));
    }

    /// <summary>
    /// Reads a bounded window of lines (1-based <paramref name="startLine"/>) so
    /// models can page through large files, mirroring the offset/limit reads that
    /// mainstream agent frameworks expose.
    /// </summary>
    private static async Task<WorkspaceFileContent> ReadLineRangeAsync(
        string fullPath,
        string relativePath,
        int? startLine,
        int? lineCount,
        CancellationToken cancellationToken)
    {
        var from = Math.Max(startLine ?? 1, 1);
        var take = lineCount is int requested && requested > 0
            ? Math.Min(requested, MaxReadLines)
            : MaxReadLines;

        var builder = new StringBuilder();
        var currentLine = 0;
        var emitted = 0;
        var lastEmittedLine = from - 1;
        var truncated = false;

        await using var stream = File.OpenRead(fullPath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            currentLine++;
            if (currentLine < from)
            {
                continue;
            }

            if (emitted >= take)
            {
                truncated = true;
                break;
            }

            if (builder.Length > MaxReadCharacters)
            {
                truncated = true;
                break;
            }

            if (emitted > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
            emitted++;
            lastEmittedLine = currentLine;
        }

        // Finish counting the total line count so the model knows the file extent.
        var totalLines = currentLine;
        while (await reader.ReadLineAsync(cancellationToken) is not null)
        {
            totalLines++;
        }

        return new WorkspaceFileContent(
            relativePath,
            builder.ToString(),
            truncated,
            emitted == 0 ? 0 : from,
            lastEmittedLine,
            totalLines);
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        foreach (var character in content)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    public async Task<WorkspaceFileWriteResult> WriteFileAsync(
        string workspaceRootPath,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "write workspace file",
            workspaceRootPath,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    throw new ArgumentException("A file path is required.", nameof(relativePath));
                }

                content ??= string.Empty;
                if (content.Length > MaxWriteCharacters)
                {
                    throw new InvalidOperationException($"The file content is too large to write safely. Limit: {MaxWriteCharacters} characters.");
                }

                var root = NormalizeRoot(workspaceRootPath);
                var fullPath = ResolvePath(root, relativePath);
                var existed = File.Exists(fullPath);
                var directoryPath = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false), cancellationToken);

                return new WorkspaceFileWriteResult(
                    Path.GetRelativePath(root, fullPath),
                    true,
                    existed,
                    content.Length,
                    existed ? "File updated." : "File created.");
            },
            ("RelativePath", relativePath),
            ("CharacterCount", content?.Length ?? 0));
    }

    public Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(
        string workspaceRootPath,
        string pattern,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            "glob workspace files",
            workspaceRootPath,
            () =>
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    throw new ArgumentException("A glob pattern is required.", nameof(pattern));
                }

                var root = NormalizeRoot(workspaceRootPath);
                var searchRoot = string.IsNullOrWhiteSpace(relativePath)
                    ? root
                    : ResolvePath(root, relativePath);
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
                foreach (var path in EnumerateSearchableFiles(searchRoot))
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
            },
            ("Pattern", pattern),
            ("RelativePath", relativePath ?? string.Empty));

    public async Task<WorkspaceFileWriteResult> EditFileAsync(
        string workspaceRootPath,
        string relativePath,
        string oldText,
        string newText,
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "edit workspace file",
            workspaceRootPath,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    throw new ArgumentException("A file path is required.", nameof(relativePath));
                }

                if (string.IsNullOrEmpty(oldText))
                {
                    throw new ArgumentException("The text to replace must not be empty.", nameof(oldText));
                }

                newText ??= string.Empty;

                var root = NormalizeRoot(workspaceRootPath);
                var fullPath = ResolvePath(root, relativePath);
                var fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists)
                {
                    throw new FileNotFoundException("File was not found.", relativePath);
                }

                if (fileInfo.Length > MaxFileBytes)
                {
                    throw new InvalidOperationException($"The file is too large to edit safely. Limit: {MaxFileBytes} bytes.");
                }

                if (!await IsTextFileAsync(fullPath, cancellationToken))
                {
                    throw new InvalidOperationException("Only text files can be edited.");
                }

                var relative = Path.GetRelativePath(root, fullPath);
                var original = await File.ReadAllTextAsync(fullPath, cancellationToken);

                // Ordinal counting so the match is exact and encoding-agnostic. A
                // unique-match contract (unless replaceAll) prevents the model from
                // silently editing the wrong occurrence.
                var occurrences = CountOccurrences(original, oldText);
                if (occurrences == 0)
                {
                    return new WorkspaceFileWriteResult(relative, false, true, original.Length, "The text to replace was not found.");
                }

                if (occurrences > 1 && !replaceAll)
                {
                    return new WorkspaceFileWriteResult(
                        relative,
                        false,
                        true,
                        original.Length,
                        $"The text to replace appears {occurrences} times. Provide more context to make it unique, or set replaceAll to replace every occurrence.");
                }

                var updated = replaceAll
                    ? original.Replace(oldText, newText, StringComparison.Ordinal)
                    : ReplaceFirst(original, oldText, newText);

                if (updated.Length > MaxWriteCharacters)
                {
                    throw new InvalidOperationException($"The edited content is too large to write safely. Limit: {MaxWriteCharacters} characters.");
                }

                await File.WriteAllTextAsync(fullPath, updated, new UTF8Encoding(false), cancellationToken);

                var replacedCount = replaceAll ? occurrences : 1;
                return new WorkspaceFileWriteResult(
                    relative,
                    true,
                    true,
                    updated.Length,
                    replacedCount == 1
                        ? "Replaced 1 occurrence."
                        : $"Replaced {replacedCount} occurrences.");
            },
            ("RelativePath", relativePath),
            ("ReplaceAll", replaceAll));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0)
        {
            return haystack;
        }

        return string.Concat(
            haystack.AsSpan(0, index),
            replacement,
            haystack.AsSpan(index + needle.Length));
    }

    public async Task<ShellCommandResult> RunShellCommandAsync(
        string workspaceRootPath,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
            "run workspace shell command",
            workspaceRootPath,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(command))
                {
                    throw new ArgumentException("A shell command is required.", nameof(command));
                }

                var root = NormalizeRoot(workspaceRootPath);
                var boundedTimeoutSeconds = Math.Clamp(timeoutSeconds, MinShellTimeoutSeconds, MaxShellTimeoutSeconds);
                var timeout = TimeSpan.FromSeconds(boundedTimeoutSeconds);

                var script = string.Join(
                    Environment.NewLine,
                    "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)",
                    "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
                    "$OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
                    command);
                var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                        WorkingDirectory = root,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to start the PowerShell process.");
                }

                var standardOutputTask = process.StandardOutput.ReadToEndAsync();
                var standardErrorTask = process.StandardError.ReadToEndAsync();

                using var registration = cancellationToken.Register(() => TryKillProcess(process));
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);

                try
                {
                    await process.WaitForExitAsync(timeoutSource.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryKillProcess(process);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    TryKillProcess(process);
                    throw new TimeoutException($"The PowerShell command timed out after {boundedTimeoutSeconds} seconds.");
                }

                var standardOutput = await standardOutputTask;
                var standardError = await standardErrorTask;
                var outputTruncated = false;
                standardOutput = TruncateShellOutput(standardOutput, ref outputTruncated);
                standardError = TruncateShellOutput(standardError, ref outputTruncated);

                var exitCode = process.ExitCode;
                return new ShellCommandResult(
                    command,
                    true,
                    exitCode,
                    standardOutput,
                    standardError,
                    outputTruncated,
                    exitCode == 0
                        ? "PowerShell command completed."
                        : $"PowerShell exited with code {exitCode}.");
            },
            ("TimeoutSeconds", timeoutSeconds),
            ("CommandLength", command?.Length ?? 0));

        if (result.ExitCode is int exitCode && exitCode != 0)
        {
            _logger.LogWarning(
                "Workspace PowerShell command exited with a non-zero code. WorkspaceRoot={WorkspaceRoot}, ExitCode={ExitCode}",
                workspaceRootPath,
                exitCode);
        }

        return result;
    }

    /// <summary>
    /// Enumerates files eligible for text search, skipping known non-productive directories
    /// and any hidden directories (name starts with a dot or has the Hidden file attribute).
    /// Uses a manual stack-based traversal to allow per-directory filtering.
    /// </summary>
    private static IEnumerable<string> EnumerateSearchableFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            // Enumerate child directories and push non-skipped ones.
            IEnumerable<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                var dirName = Path.GetFileName(subDirectory);

                // Skip dot-prefixed hidden directories (.git, .vs, .idea, etc.).
                if (dirName.StartsWith('.'))
                {
                    continue;
                }

                // Skip known build/dependency directories.
                if (SkippedDirectoryNames.Contains(dirName))
                {
                    continue;
                }

                // Skip directories marked Hidden by the OS.
                try
                {
                    var attributes = File.GetAttributes(subDirectory);
                    if (attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }

                stack.Push(subDirectory);
            }

            // Enumerate files in the current directory.
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static string NormalizeRoot(string workspaceRootPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRootPath))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRootPath));
        }

        var root = Path.GetFullPath(workspaceRootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Workspace root '{workspaceRootPath}' was not found.");
        }

        return Path.TrimEndingDirectorySeparator(root);
    }

    private static string ResolvePath(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsPathWithinRoot(root, combined))
        {
            throw new InvalidOperationException("Path traversal outside the workspace root is not allowed.");
        }

        return combined;
    }

    private static bool IsPathWithinRoot(string root, string candidatePath)
    {
        if (string.Equals(root, candidatePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsTextFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        for (var index = 0; index < read; index++)
        {
            if (buffer[index] == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string TruncateShellOutput(string value, ref bool truncated)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxShellOutputCharacters)
        {
            return value;
        }

        truncated = true;
        return value[..MaxShellOutputCharacters];
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private async Task<T> ExecuteAsync<T>(
        string operationName,
        string workspaceRootPath,
        Func<Task<T>> action,
        params (string Name, object? Value)[] properties)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Workspace operation canceled. Operation={Operation}, WorkspaceRoot={WorkspaceRoot}, Details={Details}",
                operationName,
                workspaceRootPath,
                FormatProperties(properties));
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Workspace operation failed. Operation={Operation}, WorkspaceRoot={WorkspaceRoot}, Details={Details}",
                operationName,
                workspaceRootPath,
                FormatProperties(properties));
            throw;
        }
    }

    private static string FormatProperties(IEnumerable<(string Name, object? Value)> properties)
        => string.Join(
            ", ",
            properties.Select(property => $"{property.Name}={property.Value ?? "<null>"}"));
}
