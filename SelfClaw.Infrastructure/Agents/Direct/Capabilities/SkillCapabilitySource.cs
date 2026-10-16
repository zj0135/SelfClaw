using SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// Turns the Agent's bound Skills plus the Plugin-contributed ones into this turn's instructions, loader
/// tools and message rewrites. Explicit <c>[/skill-id]</c> activation is strict — an unknown or unusable
/// token fails the turn before the provider is called — while a Skill that only appears in the compact
/// catalog degrades quietly.
/// </summary>
internal sealed class SkillCapabilitySource
{
    private const int MaximumExplicitSkills = 3;

    private readonly SkillPackageReader _packageReader;
    private readonly SkillTokenParser _tokenParser;
    private readonly SkillRuntimeToolset _runtimeToolset;
    private readonly CapabilityContentCache _contentCache;

    public SkillCapabilitySource(
        SkillPackageReader packageReader,
        SkillTokenParser tokenParser,
        SkillRuntimeToolset runtimeToolset,
        CapabilityContentCache contentCache)
    {
        _packageReader = packageReader;
        _tokenParser = tokenParser;
        _runtimeToolset = runtimeToolset;
        _contentCache = contentCache;
    }

    public async Task<SkillCapabilities> ResolveAsync(
        DirectChatTurnRequest request,
        IReadOnlyDictionary<string, ExtensionPackageRecord> installedSkills,
        IReadOnlyDictionary<string, ExtensionPackageRecord> effectiveSkills,
        IReadOnlyDictionary<string, ResolvedSkill> pluginSkills,
        TurnDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var latestUserMessage = request.Messages
            .Where(message => message.TurnId == request.TurnId &&
                              message.ConversationId == request.ConversationId &&
                              message.Role == MessageRole.User && message.Status == MessageStatus.Sealed)
            .MaxBy(message => message.Sequence);
        var explicitTokens = latestUserMessage is null
            ? []
            : _tokenParser.Parse(latestUserMessage.MarkdownContent);
        var explicitIds = ReadExplicitIds(explicitTokens);
        var resolvedSkills = await ReadEffectiveSkillsAsync(
                effectiveSkills,
                explicitIds,
                diagnostics,
                cancellationToken)
            .ConfigureAwait(false);
        var policy = DirectTurnPolicy.For(request);
        foreach (var pluginSkill in pluginSkills)
        {
            if (policy.AllowsSkill(pluginSkill.Key))
            {
                resolvedSkills.Add(pluginSkill.Key, pluginSkill.Value);
            }
        }

        foreach (var skillId in explicitIds)
        {
            ValidateExplicit(skillId, request.Agent.SkillIds, installedSkills, resolvedSkills);
        }

        var instructions = new List<string>();
        foreach (var skillId in explicitIds)
        {
            instructions.Add(CapabilitySections.Skill(resolvedSkills[skillId]));
            diagnostics.Info($"Explicitly activated Skill '{skillId}'.");
        }

        var tools = new List<DirectToolBinding>();
        if (resolvedSkills.Count > 0)
        {
            instructions.Add(CapabilitySections.SkillCatalog(resolvedSkills.Values, explicitIds));
            tools.AddRange(_runtimeToolset.CreateTools(resolvedSkills.Values.ToArray(), explicitIds));
            ReportUnavailableScripts(resolvedSkills.Values, request.Agent.ToolPolicy, diagnostics);
        }

        return new SkillCapabilities(
            instructions,
            tools,
            CreateMessageAdjustments(request.Messages, latestUserMessage, explicitTokens, resolvedSkills),
            resolvedSkills.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            explicitIds);
    }

    private string[] ReadExplicitIds(IReadOnlyList<SkillToken> explicitTokens)
    {
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
        return explicitIds.Length <= MaximumExplicitSkills
            ? explicitIds
            : throw new InvalidDataException(
                $"A turn can explicitly activate at most {MaximumExplicitSkills} Skills.");
    }

    private async Task<Dictionary<string, ResolvedSkill>> ReadEffectiveSkillsAsync(
        IReadOnlyDictionary<string, ExtensionPackageRecord> effectiveSkills,
        IReadOnlyList<string> explicitIds,
        TurnDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var resolvedSkills = new Dictionary<string, ResolvedSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in effectiveSkills.Values.OrderBy(package => package.Id, StringComparer.Ordinal))
        {
            try
            {
                var manifestPath = ExtensionInstallation.SkillManifestPath(package);
                var metadata = await _contentCache.GetSkillMetadataAsync(
                        package,
                        manifestPath,
                        token => _packageReader.ReadAsync(manifestPath, token),
                        cancellationToken)
                    .ConfigureAwait(false);
                var resources = await _contentCache.GetSkillIndexAsync(
                        package,
                        package.InstallPath,
                        token => SkillResourceIndexer.IndexAsync(package.InstallPath, token),
                        cancellationToken)
                    .ConfigureAwait(false);
                resolvedSkills[package.Id] = new ResolvedSkill(
                    package.Id,
                    metadata.Name,
                    metadata.Description,
                    metadata.Triggers,
                    package.InstallPath,
                    metadata.Content,
                    resources);
                ReportFrontMatterDiagnostics(package.Id, metadata, diagnostics);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // An unreadable Skill the user explicitly asked for must fail loudly; the rest only degrade.
                if (explicitIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Skill '{package.Id}' cannot be loaded: {exception.Message}",
                        exception);
                }

                diagnostics.Degrade(
                    $"Skill '{package.Id}' was skipped because it could not be loaded: {exception.Message}");
            }
        }

        return resolvedSkills;
    }

    /// <summary>
    /// Strips the tokens this turn consumed from the text sent to the provider. History keeps tokens that
    /// no longer match an effective Skill: rewriting those would hide what the user actually typed, and
    /// they cannot trigger activation anyway. The stored message is never modified.
    /// </summary>
    private IReadOnlyDictionary<Guid, string> CreateMessageAdjustments(
        IReadOnlyList<MessageRecord> messages,
        MessageRecord? latestUserMessage,
        IReadOnlyList<SkillToken> explicitTokens,
        IReadOnlyDictionary<string, ResolvedSkill> effectiveSkills)
    {
        var adjustments = new Dictionary<Guid, string>();
        foreach (var message in messages.Where(message => message.Role == MessageRole.User))
        {
            var consumedTokens = message.Id == latestUserMessage?.Id
                ? explicitTokens
                : _tokenParser.Parse(message.MarkdownContent)
                    .Where(token => token.IsValid && effectiveSkills.ContainsKey(token.Id))
                    .ToArray();
            if (consumedTokens.Count == 0)
            {
                continue;
            }

            adjustments[message.Id] = SkillTokenParser.RemoveTokens(message.MarkdownContent, consumedTokens);
        }

        return adjustments;
    }

    /// <summary>
    /// A policy that forbids running commands removes run_skill_script before the model ever sees it,
    /// which would otherwise read as "this Skill has no scripts". The inventory still lists them, so the
    /// gap is stated explicitly instead of leaving the model to rediscover it by trial and error.
    /// </summary>
    private static void ReportUnavailableScripts(
        IEnumerable<ResolvedSkill> skills,
        string toolPolicy,
        TurnDiagnostics diagnostics)
    {
        if (DirectCapabilityRules.Allows(ToolCallKind.Run, toolPolicy))
        {
            return;
        }

        foreach (var skill in skills)
        {
            var scriptCount = skill.Resources.Entries.Count(entry => entry.Kind == SkillResourceKind.Script);
            if (scriptCount > 0)
            {
                diagnostics.Degrade(
                    $"Skill '{skill.Id}' ships {scriptCount} script file(s), but this turn's tool policy does not allow running commands.");
            }
        }
    }

    /// <summary>
    /// The Agent Skills specification lets a Skill declare <c>allowed-tools</c>, but SelfClaw's tool policy is
    /// per turn rather than per Skill and function invocation cannot swap the tool set mid-turn. Ignoring the
    /// declaration silently would leave the author assuming a restriction that is not in force, so the
    /// mismatch is surfaced as a degradation instead.
    /// </summary>
    /// <summary>
    /// Shared with <see cref="PluginCapabilitySource"/>: a Plugin-contributed Skill parses the same
    /// front matter, so it must report the same unsupported declarations instead of passing silently.
    /// </summary>
    internal static void ReportFrontMatterDiagnostics(
        string skillId,
        SkillPackageMetadata metadata,
        TurnDiagnostics diagnostics)
    {
        if (metadata.AllowedTools.Count > 0)
        {
            diagnostics.Degrade(
                $"Skill '{skillId}' declares allowed-tools ({string.Join(", ", metadata.AllowedTools)}), which SelfClaw cannot enforce per Skill; the turn's ToolPolicy governs tool use.");
        }

        foreach (var warning in metadata.Warnings)
        {
            diagnostics.Degrade($"Skill '{skillId}': {warning}");
        }
    }

    private static void ValidateExplicit(
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

        if (!ExtensionInstallation.IsIntact(package))
        {
            throw new InvalidDataException($"Skill '{skillId}' installation is broken.");
        }
    }
}
