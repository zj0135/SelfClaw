using System.Diagnostics;
using System.Text;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools;

public sealed class WorkspaceToolService : IWorkspaceToolService
{
    private const int MaxListedEntries = 250;
    private const int MaxSearchHits = 80;
    private const int MaxReadCharacters = 24_000;
    private const int MaxWriteCharacters = 200_000;
    private const int MaxShellOutputCharacters = 24_000;
    private const int MinShellTimeoutSeconds = 1;
    private const int MaxShellTimeoutSeconds = 600;
    private const long MaxFileBytes = 1_000_000;

    public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
        string workspaceRootPath,
        string? relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = NormalizeRoot(workspaceRootPath);
        var target = ResolvePath(root, relativePath ?? string.Empty);

        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"Directory '{relativePath}' was not found.");
        }

        var entries = Directory.EnumerateFileSystemEntries(target)
            .Select(path =>
            {
                var attributes = File.GetAttributes(path);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                long? size = null;
                if (!isDirectory)
                {
                    size = new FileInfo(path).Length;
                }

                return new WorkspaceFileEntry(Path.GetRelativePath(root, path), isDirectory, size);
            })
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListedEntries)
            .ToArray();

        return Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>(entries);
    }

    public async Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
        string workspaceRootPath,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query must not be empty.", nameof(query));
        }

        var root = NormalizeRoot(workspaceRootPath);
        var hits = new List<WorkspaceSearchHit>(MaxSearchHits);

        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     IgnoreInaccessible = true,
                     RecurseSubdirectories = true,
                     ReturnSpecialDirectories = false
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes || !await IsTextFileAsync(path, cancellationToken))
            {
                continue;
            }

            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            var lineNumber = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                lineNumber++;
                if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                hits.Add(new WorkspaceSearchHit(Path.GetRelativePath(root, path), lineNumber, line.Trim()));
                if (hits.Count >= MaxSearchHits)
                {
                    return hits;
                }
            }
        }

        return hits;
    }

    public async Task<WorkspaceFileContent> ReadFileAsync(
        string workspaceRootPath,
        string relativePath,
        CancellationToken cancellationToken = default)
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

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var truncated = content.Length > MaxReadCharacters;
        if (truncated)
        {
            content = content[..MaxReadCharacters];
        }

        return new WorkspaceFileContent(Path.GetRelativePath(root, fullPath), content, truncated);
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
    }

    public async Task<ShellCommandResult> RunShellCommandAsync(
        string workspaceRootPath,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
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
}
