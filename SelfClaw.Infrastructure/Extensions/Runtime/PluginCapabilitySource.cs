using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

/// <summary>
/// Expands the Agent's bound Plugins into instructions, namespaced Skills and the plugin roots that
/// plugin-contributed MCP servers resolve against. A broken or unconfirmed Plugin degrades this turn
/// instead of failing it; every Plugin that does contribute holds a version lease for the turn.
/// </summary>
internal sealed class PluginCapabilitySource
{
    private readonly PluginManifestReader _manifestReader;
    private readonly SkillPackageReader _skillPackageReader;
    private readonly IPluginVersionLeaseManager _versionLeaseManager;
    private readonly CapabilityContentCache _contentCache;

    public PluginCapabilitySource(
        PluginManifestReader manifestReader,
        SkillPackageReader skillPackageReader,
        IPluginVersionLeaseManager versionLeaseManager,
        CapabilityContentCache contentCache)
    {
        _manifestReader = manifestReader;
        _skillPackageReader = skillPackageReader;
        _versionLeaseManager = versionLeaseManager;
        _contentCache = contentCache;
    }

    public async Task<PluginCapabilities> ResolveAsync(
        AgentRuntimeDefinition agent,
        IReadOnlyList<ExtensionPackageRecord> packages,
        IReadOnlyDictionary<string, ExtensionPackageRecord> effectiveStandaloneSkills,
        DirectTurnLeaseScope leases,
        TurnDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        if (agent.PluginIds.Count == 0)
        {
            return PluginCapabilities.Empty;
        }

        var instructions = new List<string>();
        var skills = new Dictionary<string, ResolvedSkill>(StringComparer.OrdinalIgnoreCase);
        var pluginRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in packages
                     .Where(package => package.Kind == ExtensionKind.Plugin &&
                                       package.IsEnabled &&
                                       agent.PluginIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
                     .OrderBy(package => package.Id, StringComparer.Ordinal))
        {
            PluginVersionLease? versionLease = null;
            try
            {
                if (!Directory.Exists(plugin.InstallPath))
                {
                    throw new InvalidDataException("installation directory is missing");
                }

                var manifest = await _contentCache.GetManifestAsync(
                        plugin,
                        token => _manifestReader.ReadAsync(
                            ExtensionInstallation.PluginManifestPath(plugin),
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(manifest.Id, plugin.Id, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("manifest id does not match the installed package");
                }

                var acknowledged = ExtensionCatalog.ReadAcknowledgedPermissions(plugin.AcknowledgedPermissionsJson);
                var missingPermissions = manifest.Permissions.Except(acknowledged, StringComparer.Ordinal).ToArray();
                if (missingPermissions.Length > 0)
                {
                    diagnostics.Degrade(
                        $"Plugin '{plugin.Id}' was skipped because permissions require confirmation: {string.Join(", ", missingPermissions)}.");
                    continue;
                }

                // The lease is taken before any package file is read so a concurrent delete cannot pull the
                // version directory out from under this turn.
                versionLease = _versionLeaseManager.Acquire(plugin.InstallPath);
                string? instructionSection = null;
                if (manifest.Contributions.DirectInstructions is not null)
                {
                    var content = await _contentCache.GetInstructionBodyAsync(
                            plugin,
                            manifest.Contributions.DirectInstructions,
                            token => File.ReadAllTextAsync(
                                Path.Combine(plugin.InstallPath, manifest.Contributions.DirectInstructions),
                                token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    instructionSection = CapabilitySections.Plugin(plugin.Id, content);
                }

                var contributedSkills = await ReadSkillsAsync(plugin, manifest, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var contributedSkill in contributedSkills)
                {
                    if (effectiveStandaloneSkills.ContainsKey(contributedSkill.Id))
                    {
                        throw new InvalidDataException(
                            $"Plugin Skill id '{contributedSkill.Id}' conflicts with an installed Skill.");
                    }

                    if (skills.ContainsKey(contributedSkill.Id))
                    {
                        throw new InvalidDataException($"Duplicate Plugin Skill id '{contributedSkill.Id}'.");
                    }
                }

                // Nothing is published until every contribution validated, so a half-expanded Plugin never
                // reaches the model.
                pluginRoots.Add(plugin.Id, plugin.InstallPath);
                if (instructionSection is not null)
                {
                    instructions.Add(instructionSection);
                }

                foreach (var contributedSkill in contributedSkills)
                {
                    skills.Add(contributedSkill.Id, contributedSkill);
                }

                // The scope owns the lease once the plugin contributed; until then the local finally
                // releases it when this plugin fails and degrades out of the turn.
                if (leases.Add(versionLease))
                {
                    versionLease = null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Degrade($"Plugin '{plugin.Id}' was skipped because it is broken: {exception.Message}");
            }
            finally
            {
                if (versionLease is not null)
                {
                    await versionLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        return new PluginCapabilities(instructions, skills, pluginRoots);
    }

    private async Task<IReadOnlyList<ResolvedSkill>> ReadSkillsAsync(
        ExtensionPackageRecord plugin,
        PluginManifest manifest,
        CancellationToken cancellationToken)
    {
        var skills = new List<ResolvedSkill>();
        foreach (var contribution in manifest.Contributions.Skills.OrderBy(skill => skill.Id, StringComparer.Ordinal))
        {
            var root = Path.Combine(plugin.InstallPath, contribution.Path);
            var metadata = await _contentCache.GetSkillMetadataAsync(
                    plugin,
                    ExtensionInstallation.SkillManifestName + "/" + contribution.Id,
                    token => _skillPackageReader.ReadAsync(
                        Path.Combine(root, ExtensionInstallation.SkillManifestName),
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            skills.Add(new ResolvedSkill(
                $"{plugin.Id}/{contribution.Id}",
                metadata.Name,
                metadata.Description,
                metadata.Triggers,
                root,
                metadata.Content));
        }

        return skills;
    }
}
