using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Extensions;

internal sealed class ExtensionPackageInstaller
{
    private const string SkillManifestName = "SKILL.md";
    private const string PluginManifestName = "plugin.json";
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly StoragePaths _storagePaths;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly SkillPackageReader _skillPackageReader;
    private readonly PluginManifestReader _pluginManifestReader;
    private readonly ExtensionPackageLimits _limits;

    public ExtensionPackageInstaller(
        StoragePaths storagePaths,
        IExtensionPackageRepository packageRepository,
        SkillPackageReader skillPackageReader,
        ExtensionPackageLimits limits)
        : this(
            storagePaths,
            packageRepository,
            skillPackageReader,
            new PluginManifestReader(limits),
            limits)
    {
    }

    public ExtensionPackageInstaller(
        StoragePaths storagePaths,
        IExtensionPackageRepository packageRepository,
        SkillPackageReader skillPackageReader,
        PluginManifestReader pluginManifestReader,
        ExtensionPackageLimits limits)
    {
        _storagePaths = storagePaths;
        _packageRepository = packageRepository;
        _skillPackageReader = skillPackageReader;
        _pluginManifestReader = pluginManifestReader;
        _limits = limits;
    }

    public async Task<ExtensionPackageInstallResult> InstallAsync(
        ExtensionKind kind,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        if (kind is not (ExtensionKind.Skill or ExtensionKind.Plugin))
        {
            throw new NotSupportedException("Only Skill and Plugin packages can be imported.");
        }

        var sourcePath = Path.GetFullPath(selectedPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected extension package was not found.", sourcePath);
        }

        var operationPath = CreateOperationPath();
        Directory.CreateDirectory(operationPath);
        try
        {
            if (kind == ExtensionKind.Plugin)
            {
                return await InstallPluginAsync(sourcePath, operationPath, cancellationToken).ConfigureAwait(false);
            }

            var payloadPath = await StageSkillPackageAsync(sourcePath, operationPath, cancellationToken).ConfigureAwait(false);
            var skillFilePath = Path.Combine(payloadPath, SkillManifestName);
            var metadata = await _skillPackageReader.ReadAsync(skillFilePath, cancellationToken)
                .ConfigureAwait(false);
            var fileCount = ValidateExtractedTree(payloadPath);
            var contentHash = await ComputeContentHashAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            var manifestJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id = metadata.Id,
                name = metadata.Name,
                description = metadata.Description,
                version = metadata.Version,
                triggers = metadata.Triggers
            });
            var installed = await CommitAsync(
                metadata,
                payloadPath,
                operationPath,
                contentHash,
                manifestJson,
                cancellationToken).ConfigureAwait(false);
            return new ExtensionPackageInstallResult(installed, fileCount);
        }
        finally
        {
            TryDeleteDirectoryWithin(operationPath, StagingRoot);
        }
    }

    private async Task<ExtensionPackageInstallResult> InstallPluginAsync(
        string sourcePath,
        string operationPath,
        CancellationToken cancellationToken)
    {
        var payloadPath = await StagePluginPackageAsync(sourcePath, operationPath, cancellationToken)
            .ConfigureAwait(false);
        var manifestPath = Path.Combine(payloadPath, PluginManifestName);
        var manifest = await _pluginManifestReader.ReadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var fileCount = ValidateExtractedTree(payloadPath);
        var contentHash = await ComputeContentHashAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var installed = await CommitPluginAsync(
                manifest,
                payloadPath,
                contentHash,
                manifestJson,
                cancellationToken)
            .ConfigureAwait(false);
        return new ExtensionPackageInstallResult(installed, fileCount);
    }

    private async Task<string> StagePluginPackageAsync(
        string sourcePath,
        string operationPath,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".selfclaw-plugin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Select a .zip file or a .selfclaw-plugin package.");
        }

        if (new FileInfo(sourcePath).Length > _limits.MaximumArchiveBytes)
        {
            throw new InvalidDataException($"Package exceeds the {_limits.MaximumArchiveBytes} byte archive limit.");
        }

        var extractedPath = Path.Combine(operationPath, "extracted");
        Directory.CreateDirectory(extractedPath);
        await ExtractArchiveAsync(sourcePath, extractedPath, cancellationToken).ConfigureAwait(false);
        var manifests = Directory.EnumerateFiles(extractedPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals(PluginManifestName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifests.Length != 1)
        {
            throw new InvalidDataException("A Plugin package must contain exactly one plugin.json file.");
        }

        return Path.GetDirectoryName(manifests[0])!;
    }

    private async Task<string> StageSkillPackageAsync(
        string sourcePath,
        string operationPath,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(sourcePath);
        if (fileName.Equals(SkillManifestName, StringComparison.OrdinalIgnoreCase))
        {
            var payloadPath = Path.Combine(operationPath, "payload");
            await CopyDirectoryAsync(
                Path.GetDirectoryName(sourcePath)!,
                payloadPath,
                cancellationToken).ConfigureAwait(false);
            return payloadPath;
        }

        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".selfclaw-skill", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Select SKILL.md, a .zip file, or a .selfclaw-skill package.");
        }

        if (new FileInfo(sourcePath).Length > _limits.MaximumArchiveBytes)
        {
            throw new InvalidDataException($"Package exceeds the {_limits.MaximumArchiveBytes} byte archive limit.");
        }

        var extractedPath = Path.Combine(operationPath, "extracted");
        Directory.CreateDirectory(extractedPath);
        await ExtractArchiveAsync(sourcePath, extractedPath, cancellationToken).ConfigureAwait(false);
        var manifests = Directory.EnumerateFiles(extractedPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals(SkillManifestName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifests.Length != 1)
        {
            throw new InvalidDataException("A Skill package must contain exactly one SKILL.md file.");
        }

        return Path.GetDirectoryName(manifests[0])!;
    }

    private async Task ExtractArchiveAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        var fileCount = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectArchiveLink(entry);
            var relativePath = ValidateRelativePath(entry.FullName);
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException($"Package contains a duplicate case-insensitive path: {entry.FullName}");
            }

            var destinationPath = ResolveWithin(destinationRoot, relativePath);
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            fileCount++;
            expandedBytes = checked(expandedBytes + entry.Length);
            ValidateCounts(fileCount, entry.Length, expandedBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyBoundedAsync(source, destination, entry.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CopyDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        var pending = new Stack<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(sourceRoot);
        long expandedBytes = 0;
        var fileCount = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var sourcePath in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparsePoint(sourcePath);
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                var safeRelativePath = ValidateRelativePath(relativePath);
                if (!paths.Add(safeRelativePath))
                {
                    throw new InvalidDataException($"Package contains a duplicate case-insensitive path: {relativePath}");
                }

                var attributes = File.GetAttributes(sourcePath);
                var destinationPath = ResolveWithin(destinationRoot, safeRelativePath);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.CreateDirectory(destinationPath);
                    pending.Push(sourcePath);
                    continue;
                }

                var length = new FileInfo(sourcePath).Length;
                fileCount++;
                expandedBytes = checked(expandedBytes + length);
                ValidateCounts(fileCount, length, expandedBytes);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await CopyBoundedAsync(source, destination, length, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ExtensionPackageRecord> CommitAsync(
        Skills.Models.SkillPackageMetadata metadata,
        string payloadPath,
        string operationPath,
        string contentHash,
        string manifestJson,
        CancellationToken cancellationToken)
    {
        var installRoot = Path.Combine(_storagePaths.AppDataDirectory, "skills");
        Directory.CreateDirectory(installRoot);
        var targetPath = ResolveWithin(installRoot, metadata.Id.Replace('/', Path.DirectorySeparatorChar));
        EnsureSafeInstallAncestors(installRoot, Path.GetDirectoryName(targetPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var backupPath = Path.Combine(operationPath, "backup");
        var previous = await _packageRepository.GetPackageAsync(ExtensionKind.Skill, metadata.Id, cancellationToken)
            .ConfigureAwait(false);
        var movedPrevious = false;
        try
        {
            if (Directory.Exists(targetPath))
            {
                Directory.Move(targetPath, backupPath);
                movedPrevious = true;
            }

            Directory.Move(payloadPath, targetPath);
            var now = DateTimeOffset.UtcNow;
            var package = new ExtensionPackageRecord(
                ExtensionKind.Skill,
                metadata.Id,
                metadata.Name,
                metadata.Version,
                metadata.Description,
                targetPath,
                contentHash,
                manifestJson,
                null,
                false,
                null,
                null,
                previous?.InstalledAtUtc ?? now,
                now);
            return await _packageRepository.UpsertPackageAsync(package, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteDirectoryWithin(targetPath, installRoot);
            if (movedPrevious && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, targetPath);
            }

            throw;
        }
    }

    private async Task<ExtensionPackageRecord> CommitPluginAsync(
        PluginManifest manifest,
        string payloadPath,
        string contentHash,
        string manifestJson,
        CancellationToken cancellationToken)
    {
        var pluginsRoot = Path.Combine(_storagePaths.AppDataDirectory, "plugins");
        Directory.CreateDirectory(pluginsRoot);
        var pluginRoot = ResolveWithin(pluginsRoot, manifest.Id);
        EnsureSafeInstallAncestors(pluginsRoot, pluginRoot);
        Directory.CreateDirectory(pluginRoot);
        var versionsRoot = Path.Combine(pluginRoot, "versions");
        Directory.CreateDirectory(versionsRoot);
        var versionHash = contentHash["sha256:".Length..];
        var versionPath = ResolveWithin(versionsRoot, versionHash);
        var createdVersion = false;
        if (!Directory.Exists(versionPath))
        {
            Directory.Move(payloadPath, versionPath);
            createdVersion = true;
        }

        var currentPath = Path.Combine(pluginRoot, "current.json");
        var previousPointer = File.Exists(currentPath)
            ? await File.ReadAllTextAsync(currentPath, cancellationToken).ConfigureAwait(false)
            : null;
        var previous = await _packageRepository.GetPackageAsync(
                ExtensionKind.Plugin,
                manifest.Id,
                cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var package = new ExtensionPackageRecord(
            ExtensionKind.Plugin,
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Description,
            versionPath,
            contentHash,
            manifestJson,
            null,
            previous?.IsEnabled ?? false,
            previous?.AcknowledgedPermissionsJson,
            previous?.AcknowledgedAtUtc,
            previous?.InstalledAtUtc ?? now,
            now);
        try
        {
            await WriteCurrentPointerAsync(currentPath, package, cancellationToken).ConfigureAwait(false);
            return await _packageRepository.UpsertPackageAsync(package, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (previousPointer is null)
            {
                File.Delete(currentPath);
            }
            else
            {
                await WriteTextAtomicallyAsync(currentPath, previousPointer, cancellationToken).ConfigureAwait(false);
            }

            if (createdVersion)
            {
                DeleteDirectoryWithin(versionPath, versionsRoot);
            }

            throw;
        }
    }

    private static Task WriteCurrentPointerAsync(
        string currentPath,
        ExtensionPackageRecord package,
        CancellationToken cancellationToken)
        => WriteTextAtomicallyAsync(
            currentPath,
            JsonSerializer.Serialize(new
            {
                contentHash = package.ContentHash,
                version = package.Version,
                directory = Path.GetFileName(package.InstallPath)
            }),
            cancellationToken);

    private static async Task WriteTextAtomicallyAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private async Task<string> ComputeContentHashAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var separator = new byte[] { 0 };
        var buffer = new byte[81920];
        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(rootPath, path), StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(separator);
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            hash.AppendData(separator);
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private int ValidateExtractedTree(string rootPath)
    {
        var count = 0;
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(path);
            if (Directory.Exists(path))
            {
                continue;
            }

            var length = new FileInfo(path).Length;
            count++;
            totalBytes = checked(totalBytes + length);
            ValidateCounts(count, length, totalBytes);
        }

        return count;
    }

    private void ValidateCounts(int fileCount, long fileBytes, long totalBytes)
    {
        if (fileCount > _limits.MaximumFileCount)
        {
            throw new InvalidDataException($"Package exceeds the {_limits.MaximumFileCount} file limit.");
        }

        if (fileBytes > _limits.MaximumFileBytes)
        {
            throw new InvalidDataException($"A package file exceeds the {_limits.MaximumFileBytes} byte limit.");
        }

        if (totalBytes > _limits.MaximumExpandedBytes)
        {
            throw new InvalidDataException($"Package exceeds the {_limits.MaximumExpandedBytes} byte expanded limit.");
        }
    }

    private async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            copied = checked(copied + read);
            if (copied > declaredLength || copied > _limits.MaximumFileBytes)
            {
                throw new InvalidDataException("A package entry expanded beyond its validated size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (copied != declaredLength)
        {
            throw new InvalidDataException("A package entry size changed while it was read.");
        }
    }

    private static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Package path is empty or absolute: {path}");
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var segments = normalized.Split('/');
        if (segments.Length == 0 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException($"Package path contains an unsafe segment: {path}");
        }

        foreach (var segment in segments)
        {
            if (segment.Contains(':') ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                ReservedFileNames.Contains(Path.GetFileNameWithoutExtension(segment)))
            {
                throw new InvalidDataException($"Package path is not safe on Windows: {path}");
            }
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string ResolveWithin(string rootPath, string relativePath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Package path escapes its destination: {relativePath}");
        }

        return candidate;
    }

    private static void RejectArchiveLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        var dosAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixMode == 0xA000 || dosAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Package entry is a symbolic link or reparse point: {entry.FullName}");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Package contains a symbolic link or reparse point: {path}");
        }
    }

    private static void EnsureSafeInstallAncestors(string rootPath, string directoryPath)
    {
        var root = Path.GetFullPath(rootPath);
        var current = Path.GetFullPath(directoryPath);
        while (current.Length >= root.Length)
        {
            if (Directory.Exists(current))
            {
                RejectReparsePoint(current);
            }

            if (current.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException("Install path escaped the Skill directory.");
        }

        throw new InvalidDataException("Install path escaped the Skill directory.");
    }

    private string CreateOperationPath()
        => Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));

    private string StagingRoot
        => Path.Combine(_storagePaths.AppDataDirectory, "staging", "extensions");

    private static void DeleteDirectoryWithin(string path, string rootPath)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete extension directory outside '{rootPath}'.");
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path, false);
            return;
        }

        Directory.Delete(path, true);
    }

    private static void TryDeleteDirectoryWithin(string path, string rootPath)
    {
        try
        {
            DeleteDirectoryWithin(path, rootPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
