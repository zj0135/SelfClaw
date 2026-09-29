using SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// Expands the Agent's bound Plugins into instructions, namespaced Skills, the plugin roots that
/// plugin-contributed MCP servers resolve against, and the turn's hooks. A broken or unconfirmed
/// Plugin degrades this turn instead of failing it; every Plugin that does contribute holds a version
/// lease for the turn. Subagent and continuation turns additionally inherit the parent's hook Plugins
/// (policy, not capability); an inherited Plugin that cannot be loaded blocks the turn.
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
        IReadOnlyList<DirectExtensionCapability> inheritedHookPlugins,
        DirectTurnLeaseScope leases,
        TurnDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var instructions = new List<string>();
        var skills = new Dictionary<string, ResolvedSkill>(StringComparer.OrdinalIgnoreCase);
        var pluginRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hooks = new List<ResolvedPluginHook>();
        var hookNotices = new List<string>();
        var resolvedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in packages
                     .Where(package => package.Kind == ExtensionKind.Plugin &&
                                       package.IsEnabled &&
                                       agent.PluginIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
                     .OrderBy(package => package.Id, StringComparer.Ordinal))
        {
            var load = await LoadPluginAsync(plugin, cancellationToken).ConfigureAwait(false);
            if (load is not { SkipReason: null, Manifest: { } manifest, Lease: { } versionLease })
            {
                var reason = load.SkipReason ?? "its manifest could not be loaded";
                diagnostics.Degrade(load.UnconfirmedPermissions
                    ? $"Plugin '{plugin.Id}' was skipped because permissions require confirmation: {reason}."
                    : $"Plugin '{plugin.Id}' was skipped because it is broken: {reason}");
                if (load.Manifest is { Contributions.Hooks.Count: > 0 })
                {
                    hookNotices.Add(HookNotes.PluginSkipped(
                        plugin.Id,
                        load.UnconfirmedPermissions ? $"permissions require confirmation: {reason}" : reason));
                }

                continue;
            }

            try
            {
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
                for (var index = 0; index < manifest.Contributions.Hooks.Count; index++)
                {
                    hooks.Add(new ResolvedPluginHook(
                        plugin.Id,
                        plugin.Version,
                        plugin.InstallPath,
                        manifest.Contributions.Hooks[index],
                        DeclarationOrder: index,
                        Inherited: false));
                }

                if (instructionSection is not null)
                {
                    instructions.Add(instructionSection);
                }

                foreach (var contributedSkill in contributedSkills)
                {
                    skills.Add(contributedSkill.Id, contributedSkill);
                }

                resolvedPluginIds.Add(plugin.Id);

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
                if (manifest is { Contributions.Hooks.Count: > 0 })
                {
                    hookNotices.Add(HookNotes.PluginSkipped(plugin.Id, exception.Message));
                }
            }
            finally
            {
                if (versionLease is not null)
                {
                    versionLease.Dispose();
                }
            }
        }

        var hookBlockReason = await ResolveInheritedHooksAsync(
                packages,
                inheritedHookPlugins,
                resolvedPluginIds,
                hooks,
                leases,
                cancellationToken)
            .ConfigureAwait(false);
        return new PluginCapabilities(
            instructions,
            skills,
            pluginRoots,
            hooks.OrderBy(hook => hook.PluginId, StringComparer.Ordinal)
                .ThenBy(hook => hook.DeclarationOrder)
                .ToArray(),
            hookNotices,
            hookBlockReason);
    }

    /// <summary>
    /// Resolves the hook Plugins captured by the parent turn that this turn does not bind itself.
    /// It contributes only the hooks and holds the version lease; a failure is fatal for the turn.
    /// </summary>
    private async Task<string?> ResolveInheritedHooksAsync(
        IReadOnlyList<ExtensionPackageRecord> packages,
        IReadOnlyList<DirectExtensionCapability> inheritedHookPlugins,
        HashSet<string> resolvedPluginIds,
        List<ResolvedPluginHook> hooks,
        DirectTurnLeaseScope leases,
        CancellationToken cancellationToken)
    {
        foreach (var inherited in inheritedHookPlugins.OrderBy(capability => capability.Id, StringComparer.Ordinal))
        {
            if (resolvedPluginIds.Contains(inherited.Id))
            {
                continue;
            }

            var plugin = packages.FirstOrDefault(package =>
                package.Kind == ExtensionKind.Plugin &&
                string.Equals(package.Id, inherited.Id, StringComparison.OrdinalIgnoreCase));
            if (!DirectCapabilityRules.IsPackageCurrent(plugin, inherited))
            {
                return HookNotes.InheritedHookPluginBlocked(inherited.Id);
            }

            var (manifest, versionLease, skipReason, _) =
                await LoadPluginAsync(plugin!, cancellationToken).ConfigureAwait(false);
            try
            {
                if (skipReason is not null || manifest is null || versionLease is null)
                {
                    return HookNotes.InheritedHookPluginBlocked(inherited.Id);
                }

                for (var index = 0; index < manifest.Contributions.Hooks.Count; index++)
                {
                    hooks.Add(new ResolvedPluginHook(
                        plugin!.Id,
                        plugin.Version,
                        plugin.InstallPath,
                        manifest.Contributions.Hooks[index],
                        DeclarationOrder: index,
                        Inherited: true));
                }

                if (leases.Add(versionLease))
                {
                    versionLease = null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return HookNotes.InheritedHookPluginBlocked(inherited.Id);
            }
            finally
            {
                versionLease?.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// Reads one Plugin's manifest and permissions and takes its version lease, so the Agent-bound path and
    /// the inherited-hook path cannot drift on integrity, identity, permission or lease rules. A skipped
    /// Plugin reports the detail and whether permissions were the cause; the caller owns the returned lease
    /// and decides whether a skip degrades or blocks the turn.
    /// </summary>
    private async Task<(PluginManifest? Manifest, IDisposable? Lease, string? SkipReason, bool UnconfirmedPermissions)>
        LoadPluginAsync(ExtensionPackageRecord plugin, CancellationToken cancellationToken)
    {
        PluginManifest? manifest = null;
        IDisposable? versionLease = null;
        try
        {
            if (!ExtensionInstallation.IsIntact(plugin))
            {
                return (null, null, "installation directory is missing", false);
            }

            manifest = await _contentCache.GetManifestAsync(
                    plugin,
                    token => _manifestReader.ReadAsync(
                        ExtensionInstallation.PluginManifestPath(plugin),
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(manifest.Id, plugin.Id, StringComparison.Ordinal))
            {
                return (manifest, null, "manifest id does not match the installed package", false);
            }

            var acknowledged = ExtensionCatalog.ReadAcknowledgedPermissions(plugin.AcknowledgedPermissionsJson);
            var missingPermissions = manifest.Permissions.Except(acknowledged, StringComparer.Ordinal).ToArray();
            if (missingPermissions.Length > 0)
            {
                return (manifest, null, string.Join(", ", missingPermissions), true);
            }

            // The lease is taken before any package file is read so a concurrent delete cannot pull the
            // version directory out from under this turn.
            versionLease = _versionLeaseManager.Acquire(plugin.InstallPath);
            return (manifest, versionLease, null, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A lease taken before the failure must not outlive the skipped Plugin.
            versionLease?.Dispose();
            return (manifest, null, exception.Message, false);
        }
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
