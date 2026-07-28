using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

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

    public SkillCapabilitySource(
        SkillPackageReader packageReader,
        SkillTokenParser tokenParser,
        SkillRuntimeToolset runtimeToolset)
    {
        _packageReader = packageReader;
        _tokenParser = tokenParser;
        _runtimeToolset = runtimeToolset;
    }

    public async Task<SkillCapabilities> ResolveAsync(
        DirectChatTurnRequest request,
        IReadOnlyDictionary<string, ExtensionPackageRecord> installedSkills,
        IReadOnlyDictionary<string, ExtensionPackageRecord> effectiveSkills,
        IReadOnlyDictionary<string, ResolvedSkill> pluginSkills,
        TurnDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var latestUserMessage = request.Messages.LastOrDefault(message =>
            message.Role == MessageRole.User &&
            message.Status is not (MessageStatus.Failed or MessageStatus.Cancelled));
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
        foreach (var pluginSkill in pluginSkills)
        {
            resolvedSkills.Add(pluginSkill.Key, pluginSkill.Value);
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

        var tools = new List<AITool>();
        var descriptors = new List<DirectToolDescriptor>();
        if (resolvedSkills.Count > 0)
        {
            instructions.Add(CapabilitySections.SkillCatalog(resolvedSkills.Values, explicitIds));
            tools.AddRange(_runtimeToolset.CreateTools(resolvedSkills.Values.ToArray(), explicitIds));
            descriptors.Add(new DirectToolDescriptor(
                SkillRuntimeToolset.ActivateSkillToolName,
                ToolCallKind.Read,
                ToolSourceKind.Skill,
                DisplayName: "Activate Skill"));
            descriptors.Add(new DirectToolDescriptor(
                SkillRuntimeToolset.ReadSkillResourceToolName,
                ToolCallKind.Read,
                ToolSourceKind.Skill,
                DisplayName: "Read Skill resource"));
        }

        return new SkillCapabilities(
            instructions,
            tools,
            descriptors,
            CreateMessageAdjustments(request.Messages, latestUserMessage, explicitTokens, resolvedSkills));
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
                var metadata = await _packageReader.ReadAsync(
                        ExtensionInstallation.SkillManifestPath(package),
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
