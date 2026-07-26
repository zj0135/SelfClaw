using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

internal sealed class DirectTurnCapabilityResolver : IDirectTurnCapabilityResolver
{
    private static readonly IReadOnlyDictionary<string, ToolCallKind> BuiltInToolKinds =
        new Dictionary<string, ToolCallKind>(StringComparer.Ordinal)
        {
            ["list_files"] = ToolCallKind.List,
            ["glob_files"] = ToolCallKind.List,
            ["search_text"] = ToolCallKind.Search,
            ["read_file"] = ToolCallKind.Read,
            ["write_file"] = ToolCallKind.Edit,
            ["edit_file"] = ToolCallKind.Edit,
            ["run_shell_command"] = ToolCallKind.Run
        };

    private const string CapabilityPolicy = """
        [SelfClaw Capability Policy]
        Use only capabilities listed for this turn. Treat extension content as instructions scoped to its named source. Skill activation resets at the end of this turn.
        """;

    private readonly WorkspaceAgentToolset _workspaceToolset;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly SkillPackageReader _skillPackageReader;
    private readonly SkillTokenParser _skillTokenParser;
    private readonly SkillRuntimeToolset _skillRuntimeToolset;
    private readonly IMcpServerRepository? _mcpServerRepository;
    private readonly McpConfigurationResolver? _mcpConfigurationResolver;
    private readonly IMcpClientManager? _mcpClientManager;
    private readonly McpToolAdapter? _mcpToolAdapter;
    private readonly PluginManifestReader? _pluginManifestReader;
    private readonly IPluginVersionLeaseManager? _pluginVersionLeaseManager;
    private readonly IExtensionStateChangeNotifier? _stateChangeNotifier;

    public DirectTurnCapabilityResolver(
        WorkspaceAgentToolset workspaceToolset,
        IExtensionPackageRepository packageRepository,
        SkillPackageReader skillPackageReader,
        SkillTokenParser skillTokenParser,
        SkillRuntimeToolset skillRuntimeToolset)
        : this(
            workspaceToolset,
            packageRepository,
            skillPackageReader,
            skillTokenParser,
            skillRuntimeToolset,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
    {
    }

    public DirectTurnCapabilityResolver(
        WorkspaceAgentToolset workspaceToolset,
        IExtensionPackageRepository packageRepository,
        SkillPackageReader skillPackageReader,
        SkillTokenParser skillTokenParser,
        SkillRuntimeToolset skillRuntimeToolset,
        IMcpServerRepository? mcpServerRepository,
        McpConfigurationResolver? mcpConfigurationResolver,
        IMcpClientManager? mcpClientManager,
        McpToolAdapter? mcpToolAdapter,
        PluginManifestReader? pluginManifestReader = null,
        IPluginVersionLeaseManager? pluginVersionLeaseManager = null,
        IExtensionStateChangeNotifier? stateChangeNotifier = null)
    {
        _workspaceToolset = workspaceToolset;
        _packageRepository = packageRepository;
        _skillPackageReader = skillPackageReader;
        _skillTokenParser = skillTokenParser;
        _skillRuntimeToolset = skillRuntimeToolset;
        _mcpServerRepository = mcpServerRepository;
        _mcpConfigurationResolver = mcpConfigurationResolver;
        _mcpClientManager = mcpClientManager;
        _mcpToolAdapter = mcpToolAdapter;
        _pluginManifestReader = pluginManifestReader;
        _pluginVersionLeaseManager = pluginVersionLeaseManager;
        _stateChangeNotifier = stateChangeNotifier;
    }

    public async Task<DirectTurnCapabilityLease> ResolveAsync(
        DirectChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var pluginLeases = new List<PluginVersionLease>();
        try
        {
            return await ResolveCoreAsync(request, pluginLeases, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposePluginLeasesAsync(pluginLeases).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DirectTurnCapabilityLease> ResolveCoreAsync(
        DirectChatTurnRequest request,
        ICollection<PluginVersionLease> pluginLeases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var tools = request.WorkspaceRoot is null
            ? new List<AITool>()
            : _workspaceToolset.CreateTools(
                request.WorkspaceRoot,
                request.ConversationId,
                request.ToolPermissionMode,
                request.ToolApprovalHandler).ToList();
        var descriptors = new[]
        {
            "list_files", "glob_files", "search_text", "read_file",
            "write_file", "edit_file", "run_shell_command"
        }.ToDictionary(
            name => name,
            name => new DirectToolDescriptor(name, BuiltInToolKinds[name], ToolSourceKind.BuiltIn),
            StringComparer.Ordinal);
        var diagnostics = new TurnDiagnostics();
        if (!string.Equals(
                request.Agent.ToolPolicy,
                AgentRuntimeDefinition.SystemToolPolicy,
                StringComparison.Ordinal))
        {
            diagnostics.Info($"Unsupported tool policy '{request.Agent.ToolPolicy}' was treated as 'system'.");
        }
        var packages = await _packageRepository.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        var installedSkills = packages
            .Where(package => package.Kind == ExtensionKind.Skill)
            .ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var effectiveSkills = installedSkills.Values
            .Where(package => package.IsEnabled &&
                              Directory.Exists(package.InstallPath) &&
                              File.Exists(Path.Combine(package.InstallPath, "SKILL.md")) &&
                              request.Agent.SkillIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var (pluginInstructions, pluginSkills, effectivePluginRoots) = await ResolvePluginsAsync(
                request,
                packages,
                effectiveSkills,
                diagnostics,
                pluginLeases,
                cancellationToken)
            .ConfigureAwait(false);
        var latestUserMessage = request.Messages.LastOrDefault(message =>
            message.Role == MessageRole.User &&
            message.Status is not (MessageStatus.Failed or MessageStatus.Cancelled));
        var explicitTokens = latestUserMessage is null
            ? []
            : _skillTokenParser.Parse(latestUserMessage.MarkdownContent);
        var invalidToken = explicitTokens.FirstOrDefault(token => !token.IsValid);
        if (invalidToken is not null)
        {
            throw new InvalidDataException(
                $"Skill token '{invalidToken.RawText}' is invalid. Use lowercase letters, digits and '-', with at most one '/'.");
        }

        var explicitIds = explicitTokens
            .Select(token => token.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (explicitIds.Length > 3)
        {
            throw new InvalidDataException("A turn can explicitly activate at most 3 Skills.");
        }

        var resolvedSkills = new Dictionary<string, ResolvedSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in effectiveSkills.Values.OrderBy(package => package.Id, StringComparer.Ordinal))
        {
            try
            {
                var metadata = await _skillPackageReader.ReadAsync(
                        Path.Combine(package.InstallPath, "SKILL.md"),
                        cancellationToken)
                    .ConfigureAwait(false);
                resolvedSkills[package.Id] = new ResolvedSkill(
                    package.Id,
                    metadata.Name,
                    metadata.Description,
                    metadata.Triggers,
                    package.InstallPath,
                    metadata.Content);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (explicitIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Skill '{package.Id}' cannot be loaded: {exception.Message}", exception);
                }

                diagnostics.Degrade($"Skill '{package.Id}' was skipped because it could not be loaded: {exception.Message}");
            }
        }

        foreach (var pluginSkill in pluginSkills)
        {
            resolvedSkills.Add(pluginSkill.Key, pluginSkill.Value);
        }

        foreach (var skillId in explicitIds)
        {
            ValidateExplicitSkill(skillId, request.Agent.SkillIds, installedSkills, resolvedSkills);
        }

        var systemInstructions = new List<string>();
        systemInstructions.AddRange(pluginInstructions);

        foreach (var skillId in explicitIds)
        {
            systemInstructions.Add(CreateSkillSection(resolvedSkills[skillId]));
            diagnostics.Info($"Explicitly activated Skill '{skillId}'.");
        }

        if (resolvedSkills.Count > 0)
        {
            systemInstructions.Add(CreateSkillCatalogSection(resolvedSkills.Values, explicitIds));
            var skillTools = _skillRuntimeToolset.CreateTools(resolvedSkills.Values.ToArray(), explicitIds);
            tools.AddRange(skillTools);
            descriptors[SkillRuntimeToolset.ActivateSkillToolName] = new DirectToolDescriptor(
                SkillRuntimeToolset.ActivateSkillToolName,
                ToolCallKind.Read,
                ToolSourceKind.Skill,
                DisplayName: "Activate Skill");
            descriptors[SkillRuntimeToolset.ReadSkillResourceToolName] = new DirectToolDescriptor(
                SkillRuntimeToolset.ReadSkillResourceToolName,
                ToolCallKind.Read,
                ToolSourceKind.Skill,
                DisplayName: "Read Skill resource");
        }

        var mcpLeases = await AddMcpToolsAsync(
                request,
                tools,
                descriptors,
                diagnostics,
                effectivePluginRoots,
                cancellationToken)
            .ConfigureAwait(false);
        if (diagnostics.Degradations.Count > 0)
        {
            systemInstructions.Add(CreateDegradationSection(diagnostics.Degradations));
        }

        if (systemInstructions.Count > 0 || mcpLeases.Count > 0)
        {
            systemInstructions.Insert(0, CapabilityPolicy);
        }

        var messageAdjustments = CreateMessageAdjustments(
            request.Messages,
            latestUserMessage,
            explicitTokens,
            resolvedSkills);
        return new DirectTurnCapabilityLease(
            systemInstructions,
            tools,
            descriptors,
            messageAdjustments,
            diagnostics.Messages,
            () => DisposeCapabilityLeasesAsync(mcpLeases, pluginLeases));
    }

    private async Task<IReadOnlyList<McpClientLease>> AddMcpToolsAsync(
        DirectChatTurnRequest request,
        ICollection<AITool> tools,
        IDictionary<string, DirectToolDescriptor> descriptors,
        TurnDiagnostics diagnostics,
        IReadOnlyDictionary<string, string> effectivePluginRoots,
        CancellationToken cancellationToken)
    {
        if ((request.Agent.McpServerIds.Count == 0 && effectivePluginRoots.Count == 0) ||
            _mcpServerRepository is null ||
            _mcpConfigurationResolver is null ||
            _mcpClientManager is null ||
            _mcpToolAdapter is null)
        {
            return [];
        }

        var servers = await _mcpServerRepository.ListMcpServersAsync(cancellationToken).ConfigureAwait(false);
        var effectiveServers = servers
            .Where(server => server.IsEnabled &&
                             (string.IsNullOrWhiteSpace(server.SourcePluginId)
                                 ? request.Agent.McpServerIds.Contains(server.Id, StringComparer.OrdinalIgnoreCase)
                                 : effectivePluginRoots.ContainsKey(server.SourcePluginId)))
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();
        var leases = new List<McpClientLease>();
        try
        {
            foreach (var server in effectiveServers)
            {
                var configuration = await _mcpConfigurationResolver.ResolveAsync(
                        server,
                        request.WorkspaceRoot?.RootPath,
                        string.IsNullOrWhiteSpace(server.SourcePluginId)
                            ? null
                            : effectivePluginRoots.GetValueOrDefault(server.SourcePluginId),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!configuration.IsAvailable)
                {
                    diagnostics.Degrade($"MCP server '{server.Id}' was skipped: {configuration.UnavailableReason}");
                    await TryRecordMcpHealthAsync(
                            server,
                            McpServerHealthStatus.NeedsConfiguration,
                            configuration.UnavailableReason,
                            [],
                            diagnostics,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                McpClientLease lease;
                try
                {
                    lease = await _mcpClientManager.AcquireAsync(configuration, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    const string failure = "Connection or tool discovery failed.";
                    diagnostics.Degrade($"MCP server '{server.Id}' was skipped: {failure}");
                    await TryRecordMcpHealthAsync(
                            server,
                            McpServerHealthStatus.Degraded,
                            failure,
                            [],
                            diagnostics,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                leases.Add(lease);
                await TryRecordMcpHealthAsync(
                        server,
                        McpServerHealthStatus.Ready,
                        null,
                        lease.Tools.Select(tool => tool.Name).ToArray(),
                        diagnostics,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var mcpTool in lease.Tools)
                {
                    var adapted = _mcpToolAdapter.Create(
                        mcpTool,
                        configuration,
                        request.ConversationId,
                        request.ToolPermissionMode,
                        request.ToolApprovalHandler);
                    if (descriptors.ContainsKey(adapted.Descriptor.ProviderName))
                    {
                        throw new InvalidDataException(
                            $"MCP tool name collision for '{adapted.Descriptor.ProviderName}'.");
                    }

                    tools.Add(adapted.Tool);
                    descriptors.Add(adapted.Descriptor.ProviderName, adapted.Descriptor);
                }
            }

            return leases;
        }
        catch
        {
            await DisposeMcpLeasesAsync(leases).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(
        IReadOnlyList<string> Instructions,
        IReadOnlyDictionary<string, ResolvedSkill> Skills,
        IReadOnlyDictionary<string, string> PluginRoots)> ResolvePluginsAsync(
        DirectChatTurnRequest request,
        IReadOnlyList<ExtensionPackageRecord> packages,
        IReadOnlyDictionary<string, ExtensionPackageRecord> effectiveStandaloneSkills,
        TurnDiagnostics diagnostics,
        ICollection<PluginVersionLease> pluginLeases,
        CancellationToken cancellationToken)
    {
        if (request.Agent.PluginIds.Count == 0 || _pluginManifestReader is null)
        {
            return ([], new Dictionary<string, ResolvedSkill>(), new Dictionary<string, string>());
        }

        var instructions = new List<string>();
        var skills = new Dictionary<string, ResolvedSkill>(StringComparer.OrdinalIgnoreCase);
        var pluginRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in packages
                     .Where(package => package.Kind == ExtensionKind.Plugin &&
                                       package.IsEnabled &&
                                       request.Agent.PluginIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
                     .OrderBy(package => package.Id, StringComparer.Ordinal))
        {
            PluginVersionLease? versionLease = null;
            try
            {
                if (!Directory.Exists(plugin.InstallPath))
                {
                    throw new InvalidDataException("installation directory is missing");
                }

                var manifest = await _pluginManifestReader.ReadAsync(
                        Path.Combine(plugin.InstallPath, "plugin.json"),
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

                versionLease = _pluginVersionLeaseManager?.Acquire(plugin.InstallPath);
                string? instructionSection = null;
                if (manifest.Contributions.DirectInstructions is not null)
                {
                    var content = await File.ReadAllTextAsync(
                            Path.Combine(plugin.InstallPath, manifest.Contributions.DirectInstructions),
                            cancellationToken)
                        .ConfigureAwait(false);
                    instructionSection = CreatePluginSection(plugin.Id, content);
                }

                var contributedSkills = await ReadPluginSkillsAsync(plugin, manifest, cancellationToken)
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

                pluginRoots.Add(plugin.Id, plugin.InstallPath);
                if (instructionSection is not null)
                {
                    instructions.Add(instructionSection);
                }

                foreach (var contributedSkill in contributedSkills)
                {
                    skills.Add(contributedSkill.Id, contributedSkill);
                }

                if (versionLease is not null)
                {
                    pluginLeases.Add(versionLease);
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

        return (instructions, skills, pluginRoots);
    }

    private async Task<IReadOnlyList<ResolvedSkill>> ReadPluginSkillsAsync(
        ExtensionPackageRecord plugin,
        PluginManifest manifest,
        CancellationToken cancellationToken)
    {
        var skills = new List<ResolvedSkill>();
        foreach (var contribution in manifest.Contributions.Skills.OrderBy(skill => skill.Id, StringComparer.Ordinal))
        {
            var canonicalId = $"{plugin.Id}/{contribution.Id}";
            var root = Path.Combine(plugin.InstallPath, contribution.Path);
            var metadata = await _skillPackageReader.ReadAsync(Path.Combine(root, "SKILL.md"), cancellationToken)
                .ConfigureAwait(false);
            skills.Add(new ResolvedSkill(
                canonicalId,
                metadata.Name,
                metadata.Description,
                metadata.Triggers,
                root,
                metadata.Content));
        }

        return skills;
    }

    private async Task TryRecordMcpHealthAsync(
        McpServerConfigRecord server,
        McpServerHealthStatus status,
        string? error,
        IReadOnlyList<string> tools,
        TurnDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        if (_mcpServerRepository is null)
        {
            return;
        }

        try
        {
            _ = await _mcpServerRepository.UpsertMcpServerAsync(
                    server with
                    {
                        DiscoveredTools = tools,
                        LastStatus = status,
                        LastError = error,
                        LastCheckedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            _stateChangeNotifier?.Advance();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            diagnostics.Info($"MCP server '{server.Id}' health could not be persisted.");
        }
    }

    private static async ValueTask DisposeMcpLeasesAsync(IReadOnlyList<McpClientLease> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposePluginLeasesAsync(IEnumerable<PluginVersionLease> leases)
    {
        foreach (var lease in leases.Reverse())
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeCapabilityLeasesAsync(
        IReadOnlyList<McpClientLease> mcpLeases,
        IEnumerable<PluginVersionLease> pluginLeases)
    {
        try
        {
            await DisposeMcpLeasesAsync(mcpLeases).ConfigureAwait(false);
        }
        finally
        {
            await DisposePluginLeasesAsync(pluginLeases).ConfigureAwait(false);
        }
    }

    private IReadOnlyDictionary<Guid, string> CreateMessageAdjustments(
        IReadOnlyList<MessageRecord> messages,
        MessageRecord? latestUserMessage,
        IReadOnlyList<SkillToken> explicitTokens,
        IReadOnlyDictionary<string, ResolvedSkill> effectiveSkills)
    {
        var adjustments = new Dictionary<Guid, string>();
        foreach (var message in messages.Where(message => message.Role == MessageRole.User))
        {
            IReadOnlyList<SkillToken> consumedTokens;
            if (message.Id == latestUserMessage?.Id)
            {
                consumedTokens = explicitTokens;
            }
            else
            {
                consumedTokens = _skillTokenParser.Parse(message.MarkdownContent)
                    .Where(token => token.IsValid && effectiveSkills.ContainsKey(token.Id))
                    .ToArray();
            }

            if (consumedTokens.Count == 0)
            {
                continue;
            }

            adjustments[message.Id] = SkillTokenParser.RemoveTokens(message.MarkdownContent, consumedTokens);
        }

        return adjustments;
    }

    private static void ValidateExplicitSkill(
        string skillId,
        IReadOnlyList<string> agentSkillIds,
        IReadOnlyDictionary<string, ExtensionPackageRecord> installedSkills,
        IReadOnlyDictionary<string, ResolvedSkill> resolvedSkills)
    {
        if (resolvedSkills.ContainsKey(skillId))
        {
            return;
        }

        if (!installedSkills.TryGetValue(skillId, out var package))
        {
            throw new InvalidDataException($"Skill '{skillId}' is not installed.");
        }

        if (!package.IsEnabled)
        {
            throw new InvalidDataException($"Skill '{skillId}' is disabled in extension settings.");
        }

        if (!agentSkillIds.Contains(skillId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Skill '{skillId}' is not bound to the current Agent.");
        }

        if (!Directory.Exists(package.InstallPath) ||
            !File.Exists(Path.Combine(package.InstallPath, "SKILL.md")))
        {
            throw new InvalidDataException($"Skill '{skillId}' installation is broken.");
        }
    }

    private static string CreateDegradationSection(IReadOnlyList<string> degradations)
        => $"""
            [SelfClaw Capability Degradation]
            {string.Join("\n", degradations.Select(degradation => $"- {degradation}"))}
            Do not claim these capabilities are available in this turn.
            """;

    private static string CreateSkillSection(ResolvedSkill skill)
        => $"""
            [SelfClaw Skill: {skill.Id}]
            ----- BEGIN SELFCLAW SKILL {skill.Id} -----
            {skill.Content}
            ----- END SELFCLAW SKILL {skill.Id} -----
            """;

    private static string CreatePluginSection(string pluginId, string content)
        => $"""
            [SelfClaw Plugin: {pluginId}]
            ----- BEGIN SELFCLAW PLUGIN {pluginId} -----
            {content}
            ----- END SELFCLAW PLUGIN {pluginId} -----
            """;

    private static string CreateSkillCatalogSection(
        IEnumerable<ResolvedSkill> skills,
        IReadOnlyList<string> explicitlyActivatedSkillIds)
    {
        var lines = skills
            .Where(skill => !explicitlyActivatedSkillIds.Contains(skill.Id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(skill => skill.Id, StringComparer.Ordinal)
            .Select(skill =>
            {
                var triggers = skill.Triggers.Count == 0
                    ? string.Empty
                    : $" Triggers: {string.Join(", ", skill.Triggers)}.";
                return $"- {skill.Id}: {skill.Name} - {skill.Description}.{triggers}";
            })
            .ToArray();
        var catalog = lines.Length == 0 ? "- No additional inactive Skills." : string.Join("\n", lines);
        return $"""
            [SelfClaw Available Skills]
            {catalog}
            Use activate_skill to load an available Skill. Use read_skill_resource only after that Skill is activated. Activation state resets each turn.
            """;
    }

}
