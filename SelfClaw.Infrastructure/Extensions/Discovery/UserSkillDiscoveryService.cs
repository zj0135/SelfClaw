using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Skills;

namespace SelfClaw.Infrastructure.Extensions.Discovery;

/// <summary>
/// Discovers skills the user drops into <c>%UserProfile%\.agents\skills\&lt;id&gt;\SKILL.md</c> at startup and
/// registers them as in-place <see cref="ExtensionPackageRecord"/> rows. Files are referenced from the source
/// directory, never copied into <c>%LocalAppData%\SelfClaw\skills</c>; the runtime re-reads <c>SKILL.md</c>
/// each turn, so edits to a source file take effect on the next turn. A newly discovered skill is registered
/// disabled; a re-scan of an unchanged skill skips the upsert entirely, preserving the user's enabled state.
/// </summary>
internal sealed class UserSkillDiscoveryService
{
    private readonly string _userSkillsRoot;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly SkillPackageReader _skillPackageReader;
    private readonly ILogger<UserSkillDiscoveryService> _logger;

    public UserSkillDiscoveryService(
        string userSkillsRoot,
        IExtensionPackageRepository packageRepository,
        SkillPackageReader skillPackageReader,
        ILogger<UserSkillDiscoveryService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSkillsRoot);
        ArgumentNullException.ThrowIfNull(packageRepository);
        ArgumentNullException.ThrowIfNull(skillPackageReader);
        ArgumentNullException.ThrowIfNull(logger);
        _userSkillsRoot = userSkillsRoot;
        _packageRepository = packageRepository;
        _skillPackageReader = skillPackageReader;
        _logger = logger;
    }

    public static string DefaultUserSkillsRoot
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agents",
            "skills");

    public async Task DiscoverAndRegisterAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_userSkillsRoot))
        {
            _logger.LogDebug(
                "User skills root '{UserSkillsRoot}' does not exist; nothing to discover.",
                _userSkillsRoot);
            return;
        }

        string[] skillFiles;
        try
        {
            skillFiles = Directory.EnumerateFiles(
                    _userSkillsRoot,
                    ExtensionInstallation.SkillManifestName,
                    SearchOption.AllDirectories)
                .ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(
                exception,
                "Failed to enumerate '{UserSkillsRoot}' for skill discovery.",
                _userSkillsRoot);
            return;
        }

        foreach (var skillFilePath in skillFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RegisterSkillAsync(skillFilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A single broken SKILL.md must not abort the whole scan.
                _logger.LogWarning(
                    exception,
                    "Skipped skill at '{SkillFilePath}' because it could not be registered.",
                    skillFilePath);
            }
        }
    }

    private async Task RegisterSkillAsync(string skillFilePath, CancellationToken cancellationToken)
    {
        var skillDirectory = Path.GetDirectoryName(skillFilePath)!;
        var metadata = await _skillPackageReader.ReadAsync(skillFilePath, cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var contentHash = ComputeContentHash(metadata.Content);
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = metadata.Id,
            name = metadata.Name,
            description = metadata.Description,
            version = metadata.Version,
            triggers = metadata.Triggers
        });

        var existing = await _packageRepository.GetPackageAsync(ExtensionKind.Skill, metadata.Id, cancellationToken)
            .ConfigureAwait(false);

        // A skill registered through the package-import flow lives under %LocalAppData%; an in-place
        // discovery must not clobber that record. Discovery only owns records pointing at its source dirs.
        if (existing is not null &&
            !string.Equals(existing.InstallPath, skillDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Skill '{SkillId}' is already registered at '{ExistingPath}'; skipping in-place registration from '{SkillFilePath}'.",
                metadata.Id, existing.InstallPath, skillFilePath);
            return;
        }

        // Unchanged since the last scan: skip the upsert so IsEnabled, acknowledged permissions and
        // timestamps stay exactly as the user left them.
        if (existing is not null &&
            string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal))
        {
            return;
        }

        var package = new ExtensionPackageRecord(
            ExtensionKind.Skill,
            metadata.Id,
            metadata.Name,
            metadata.Version,
            metadata.Description,
            skillDirectory,
            contentHash,
            manifestJson,
            SourcePluginId: null,
            IsEnabled: existing?.IsEnabled ?? false,
            existing?.AcknowledgedPermissionsJson,
            existing?.AcknowledgedAtUtc,
            existing?.InstalledAtUtc ?? now,
            now);
        await _packageRepository.UpsertPackageAsync(package, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Registered user skill '{SkillId}' from '{SkillFilePath}' ({Mode}).",
            metadata.Id, skillFilePath, existing is null ? "new" : "updated");
    }

    private static string ComputeContentHash(string markdown)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(markdown));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
