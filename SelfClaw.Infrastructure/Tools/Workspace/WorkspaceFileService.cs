using System.Text;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal sealed class WorkspaceFileService
{
    private const int MaxListedEntries = 250;
    private const int MaxReadCharacters = 24_000;
    private const int MaxReadLines = 2_000;
    private const int MaxWriteCharacters = 200_000;

    public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
        string workspaceRootPath,
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = WorkspaceFileAccess.NormalizeRoot(workspaceRootPath);
        var target = WorkspaceFileAccess.ResolvePath(root, relativePath ?? string.Empty);

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
            .Where(info => !WorkspaceFileAccess.IsIgnoredListingEntry(info))
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
    }

    public async Task<WorkspaceFileContent> ReadFileAsync(
        string workspaceRootPath,
        string relativePath,
        int? startLine = null,
        int? lineCount = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A file path is required.", nameof(relativePath));
        }

        var root = WorkspaceFileAccess.NormalizeRoot(workspaceRootPath);
        var fullPath = await WorkspaceFileAccess.ResolveTextFileAsync(root, relativePath, "read", cancellationToken)
            .ConfigureAwait(false);
        var relative = Path.GetRelativePath(root, fullPath);

        // Line-ranged read: stream the file so a large file can be paged
        // without loading it all. startLine is 1-based; lineCount defaults
        // to MaxReadLines from the start line.
        if (startLine is not null || lineCount is not null)
        {
            return await ReadLineRangeAsync(fullPath, relative, startLine, lineCount, cancellationToken).ConfigureAwait(false);
        }

        // Whole-file read (legacy behaviour) with a character-count safety cap.
        // CRLF/CR is normalized to LF so the model sees the same line-ending shape
        // that ranged reads and edit_file operate on — the file's on-disk convention
        // is preserved by edit_file on write-back, not by the read path.
        var content = WorkspaceTextEditor.NormalizeToLf(await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false));
        var totalLines = CountLines(content);
        var truncated = content.Length > MaxReadCharacters;
        if (truncated)
        {
            content = content[..MaxReadCharacters];
        }

        var endLine = truncated ? CountLines(content) : totalLines;
        return new WorkspaceFileContent(relative, content, truncated, 1, endLine, totalLines);
    }

    public async Task<WorkspaceFileWriteResult> WriteFileAsync(
        string workspaceRootPath,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
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

        var root = WorkspaceFileAccess.NormalizeRoot(workspaceRootPath);
        var fullPath = WorkspaceFileAccess.ResolvePath(root, relativePath);
        var existed = File.Exists(fullPath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        return new WorkspaceFileWriteResult(
            Path.GetRelativePath(root, fullPath),
            true,
            existed,
            content.Length,
            existed ? "File updated." : "File created.");
    }

    public async Task<WorkspaceFileWriteResult> EditFileAsync(
        string workspaceRootPath,
        string relativePath,
        string oldText,
        string newText,
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
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

        var root = WorkspaceFileAccess.NormalizeRoot(workspaceRootPath);
        var fullPath = await WorkspaceFileAccess.ResolveTextFileAsync(root, relativePath, "edit", cancellationToken)
            .ConfigureAwait(false);
        var relative = Path.GetRelativePath(root, fullPath);
        var original = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);

        var edit = WorkspaceTextEditor.Apply(original, oldText, newText, replaceAll);
        if (edit.Error is not null)
        {
            return new WorkspaceFileWriteResult(relative, false, true, original.Length, edit.Error);
        }

        return await CommitEditAsync(
            fullPath, relative, edit.Content, edit.LineEnding, edit.Replacements, cancellationToken).ConfigureAwait(false);
    }

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
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
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
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
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

    private static async Task<WorkspaceFileWriteResult> CommitEditAsync(
        string fullPath,
        string relative,
        string lfContent,
        string lineEnding,
        int replacedCount,
        CancellationToken cancellationToken)
    {
        if (lfContent.Length > MaxWriteCharacters)
        {
            throw new InvalidOperationException($"The edited content is too large to write safely. Limit: {MaxWriteCharacters} characters.");
        }

        var finalText = lineEnding == "\r\n"
            ? lfContent.Replace("\n", "\r\n")
            : lfContent;

        await File.WriteAllTextAsync(fullPath, finalText, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        return new WorkspaceFileWriteResult(
            relative,
            true,
            true,
            finalText.Length,
            replacedCount == 1
                ? "Replaced 1 occurrence."
                : $"Replaced {replacedCount} occurrences.");
    }
}
