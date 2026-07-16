using System.IO;
using System.Text;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopAgentStore
{
    public const string BuildAgentId = "build";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly string _agentsDirectory;
    private readonly object _syncRoot = new();

    public DesktopAgentStore(StoragePaths storagePaths)
    {
        _agentsDirectory = Path.Combine(storagePaths.AppDataDirectory, "agents");
    }

    public string AgentsDirectory => _agentsDirectory;

    public IReadOnlyList<DesktopAgentDefinition> LoadAll()
    {
        lock (_syncRoot)
        {
            EnsureSystemAgents();

            var installedSkillIds = DiscoverInstalledSkillIds();
            var agents = new List<DesktopAgentDefinition>();
            foreach (var filePath in Directory.EnumerateFiles(_agentsDirectory, "*.md", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    agents.Add(LoadAgentFromFile(filePath, installedSkillIds));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    var rawId = Path.GetFileNameWithoutExtension(filePath);
                    agents.Add(new DesktopAgentDefinition(
                        string.IsNullOrWhiteSpace(rawId) ? "invalid-agent" : rawId,
                        string.IsNullOrWhiteSpace(rawId) ? "invalid-agent" : rawId,
                        string.Empty,
                        AgentExecutionMode.Direct,
                        AgentRuntimeDefinition.SystemToolPolicy,
                        [],
                        [],
                        [],
                        [],
                        string.Empty,
                        filePath,
                        false,
                        [$"Unable to load agent file: {exception.Message}"]));
                }
            }

            return agents
                .OrderBy(item => GetAgentSortOrder(item.Id))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private DesktopAgentDefinition LoadAgentFromFile(
        string filePath,
        ISet<string> installedSkillIds)
    {
        var agentId = NormalizeAgentId(Path.GetFileNameWithoutExtension(filePath));
        if (!IsValidAgentId(agentId))
        {
            throw new InvalidOperationException($"Agent file '{filePath}' has an invalid file name.");
        }

        string markdown;
        try
        {
            markdown = File.ReadAllText(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DesktopAgentDefinition(
                agentId,
                agentId,
                string.Empty,
                AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                [],
                string.Empty,
                filePath,
                IsBuiltInAgentId(agentId),
                [$"Unable to read agent file: {exception.Message}"]);
        }

        var parseResult = ParseAgentMarkdown(agentId, markdown);
        var warnings = parseResult.Warnings.ToList();

        foreach (var skillId in parseResult.Skills)
        {
            if (!installedSkillIds.Contains(skillId))
            {
                warnings.Add($"Skill '{skillId}' is not installed and will be ignored at runtime.");
            }
        }

        return new DesktopAgentDefinition(
            agentId,
            parseResult.Name,
            parseResult.Description,
            parseResult.Mode,
            parseResult.ToolPolicy,
            parseResult.Skills,
            parseResult.DisabledSkills,
            parseResult.McpServers,
            parseResult.DisabledMcpServers,
            parseResult.Instructions,
            filePath,
            IsBuiltInAgentId(agentId),
            warnings);
    }

    private void EnsureSystemAgents()
    {
        Directory.CreateDirectory(_agentsDirectory);

        EnsureSystemAgentFile(new DesktopAgentDefinition(
            BuildAgentId,
            BuildAgentId,
            "通用代理（默认）",
            AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            [],
            """
            You are the default build agent for SelfClaw.
            Work directly in the selected workspace, keep changes scoped, and verify important outcomes before you conclude.
            """,
            GetAgentFilePath(BuildAgentId),
            true,
            []));

    }

    private void EnsureSystemAgentFile(DesktopAgentDefinition definition)
    {
        if (File.Exists(definition.FilePath))
        {
            return;
        }

        WriteAgentFile(definition);
    }

    private void WriteAgentFile(DesktopAgentDefinition definition)
    {
        Directory.CreateDirectory(_agentsDirectory);
        File.WriteAllText(definition.FilePath, SerializeAgentMarkdown(definition), Utf8WithoutBom);
    }

    private static string SerializeAgentMarkdown(DesktopAgentDefinition definition)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"name: {EscapeScalar(definition.Name)}");
        builder.AppendLine($"description: {EscapeScalar(definition.Description)}");
        builder.AppendLine($"mode: {ToModeId(definition.Mode)}");
        builder.AppendLine($"tools: {NormalizeToolPolicy(definition.ToolPolicy)}");
        builder.AppendLine("skills:");
        foreach (var skillId in definition.Skills)
        {
            builder.AppendLine($"  - {skillId}");
        }

        builder.AppendLine("disabledSkills:");
        foreach (var skillId in definition.DisabledSkills)
        {
            builder.AppendLine($"  - {skillId}");
        }

        builder.AppendLine("mcpServers:");
        foreach (var serverId in definition.McpServers)
        {
            builder.AppendLine($"  - {serverId}");
        }

        builder.AppendLine("disabledMcpServers:");
        foreach (var serverId in definition.DisabledMcpServers)
        {
            builder.AppendLine($"  - {serverId}");
        }

        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append(NormalizeInstructions(definition.Instructions));
        builder.AppendLine();
        return builder.ToString();
    }

    private static string EscapeScalar(string? value)
        => $"\"{(value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private AgentParseResult ParseAgentMarkdown(string agentId, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new AgentParseResult(
                agentId,
                string.Empty,
                AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                [],
                string.Empty,
                ["Agent file is empty; using default values."]);
        }

        var normalized = markdown.ReplaceLineEndings("\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new AgentParseResult(
                agentId,
                string.Empty,
                AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                [],
                normalized.Trim(),
                ["Front matter is missing; using default metadata."]);
        }

        var endIndex = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return new AgentParseResult(
                agentId,
                string.Empty,
                AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                [],
                normalized.Trim(),
                ["Front matter is incomplete; using default metadata."]);
        }

        var metadataBlock = normalized[4..endIndex];
        var instructions = normalized[(endIndex + 5)..].Trim();
        var warnings = new List<string>();
        var name = agentId;
        var description = string.Empty;
        var mode = AgentExecutionMode.Direct;
        var toolPolicy = AgentRuntimeDefinition.SystemToolPolicy;
        var skills = new List<string>();
        var disabledSkills = new List<string>();
        var mcpServers = new List<string>();
        var disabledMcpServers = new List<string>();
        string? currentList = null;

        foreach (var rawLine in metadataBlock.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (currentList is null)
                {
                    warnings.Add($"Ignoring list item '{trimmed}' because it is not attached to a known key.");
                    continue;
                }

                var listValue = Unquote(trimmed[2..].Trim());
                if (currentList == "skills")
                {
                    var normalizedSkillId = NormalizeSkillId(listValue);
                    if (!string.IsNullOrWhiteSpace(normalizedSkillId))
                    {
                        skills.Add(normalizedSkillId);
                    }
                }
                else if (currentList == "disabledSkills")
                {
                    var normalizedSkillId = NormalizeSkillId(listValue);
                    if (!string.IsNullOrWhiteSpace(normalizedSkillId))
                    {
                        disabledSkills.Add(normalizedSkillId);
                    }
                }
                else if (currentList == "mcpServers")
                {
                    var normalizedServerId = NormalizeMcpServerId(listValue);
                    if (!string.IsNullOrWhiteSpace(normalizedServerId))
                    {
                        mcpServers.Add(normalizedServerId);
                    }
                }
                else if (currentList == "disabledMcpServers")
                {
                    var normalizedServerId = NormalizeMcpServerId(listValue);
                    if (!string.IsNullOrWhiteSpace(normalizedServerId))
                    {
                        disabledMcpServers.Add(normalizedServerId);
                    }
                }

                continue;
            }

            currentList = null;
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                warnings.Add($"Ignoring malformed front matter line '{line}'.");
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            switch (key)
            {
                case "name":
                    name = string.IsNullOrWhiteSpace(value) ? agentId : Unquote(value);
                    break;
                case "description":
                    description = Unquote(value);
                    break;
                case "mode":
                    mode = ParseMode(value, warnings);
                    break;
                case "tools":
                    toolPolicy = NormalizeToolPolicy(value);
                    if (!string.Equals(toolPolicy, AgentRuntimeDefinition.SystemToolPolicy, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"Unsupported tools value '{value}'. Using '{AgentRuntimeDefinition.SystemToolPolicy}'.");
                        toolPolicy = AgentRuntimeDefinition.SystemToolPolicy;
                    }

                    break;
                case "skills":
                    currentList = "skills";
                    break;
                case "disabledSkills":
                    currentList = "disabledSkills";
                    break;
                case "mcpServers":
                    currentList = "mcpServers";
                    break;
                case "disabledMcpServers":
                    currentList = "disabledMcpServers";
                    break;
                default:
                    warnings.Add($"Ignoring unsupported front matter key '{key}'.");
                    break;
            }
        }

        var normalizedSkills = skills
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedDisabledSkills = NormalizeSelectedSubset(disabledSkills, normalizedSkills, NormalizeSkillId);
        var normalizedMcpServers = mcpServers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedDisabledMcpServers = NormalizeSelectedSubset(disabledMcpServers, normalizedMcpServers, NormalizeMcpServerId);

        return new AgentParseResult(
            string.IsNullOrWhiteSpace(name) ? agentId : name.Trim(),
            description.Trim(),
            mode,
            toolPolicy,
            normalizedSkills,
            normalizedDisabledSkills,
            normalizedMcpServers,
            normalizedDisabledMcpServers,
            NormalizeInstructions(instructions),
            warnings);
    }

    private static AgentExecutionMode ParseMode(string value, ICollection<string> warnings)
    {
        var normalized = Unquote(value).Trim();
        if (string.Equals(normalized, "plan", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Mode 'plan' is no longer supported. Using 'direct'.");
            return AgentExecutionMode.Direct;
        }

        if (string.Equals(normalized, "cli", StringComparison.OrdinalIgnoreCase))
        {
            return AgentExecutionMode.Cli;
        }

        if (!string.IsNullOrWhiteSpace(normalized) &&
            !string.Equals(normalized, "direct", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Unsupported mode '{normalized}'. Using 'direct'.");
        }

        return AgentExecutionMode.Direct;
    }

    private static string ToModeId(AgentExecutionMode mode)
        => mode == AgentExecutionMode.Cli ? "cli" : "direct";

    private static string NormalizeInstructions(string? instructions)
        => string.IsNullOrWhiteSpace(instructions)
            ? string.Empty
            : instructions.ReplaceLineEndings("\n").Trim();

    private static string NormalizeToolPolicy(string? toolPolicy)
    {
        var normalized = toolPolicy?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? AgentRuntimeDefinition.SystemToolPolicy
            : normalized;
    }

    private static IReadOnlyList<string> NormalizeSelectedSubset(
        IEnumerable<string>? values,
        IReadOnlyList<string> selectedValues,
        Func<string?, string> normalize)
    {
        if (selectedValues.Count == 0)
        {
            return [];
        }

        var selected = selectedValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (values ?? [])
            .Select(normalize)
            .Where(item => !string.IsNullOrWhiteSpace(item) && selected.Contains(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private HashSet<string> DiscoverInstalledSkillIds()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skillsRoot = Path.Combine(Path.GetDirectoryName(_agentsDirectory)!, "skills");
        if (!Directory.Exists(skillsRoot))
        {
            return results;
        }

        foreach (var skillFilePath in Directory.EnumerateFiles(skillsRoot, "SKILL.md", SearchOption.AllDirectories))
        {
            var skillDirectory = Path.GetDirectoryName(skillFilePath);
            if (string.IsNullOrWhiteSpace(skillDirectory))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(skillsRoot, skillDirectory);
            var skillId = NormalizeSkillId(relativePath == "." ? new DirectoryInfo(skillDirectory).Name : relativePath);
            if (!string.IsNullOrWhiteSpace(skillId))
            {
                results.Add(skillId);
            }
        }

        return results;
    }

    private string GetAgentFilePath(string agentId)
        => Path.Combine(_agentsDirectory, $"{agentId}.md");

    private static bool IsBuiltInAgentId(string agentId)
        => string.Equals(agentId, BuildAgentId, StringComparison.OrdinalIgnoreCase);

    private static int GetAgentSortOrder(string agentId)
        => agentId.ToLowerInvariant() switch
        {
            BuildAgentId => 0,
            _ => 2
        };

    private static string NormalizeAgentId(string? agentId)
        => agentId?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeMcpServerId(string? serverId)
        => serverId?.Trim() ?? string.Empty;

    private static string NormalizeSkillId(string? skillId)
    {
        var normalized = (skillId ?? string.Empty).Replace('\\', '/').Trim('/');
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item is not "." and not "..")
            .ToArray();

        return string.Join("/", segments);
    }

    private static bool IsValidAgentId(string agentId)
        => !string.IsNullOrWhiteSpace(agentId) &&
           agentId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1]
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        return value;
    }

    private sealed record AgentParseResult(
        string Name,
        string Description,
        AgentExecutionMode Mode,
        string ToolPolicy,
        IReadOnlyList<string> Skills,
        IReadOnlyList<string> DisabledSkills,
        IReadOnlyList<string> McpServers,
        IReadOnlyList<string> DisabledMcpServers,
        string Instructions,
        IReadOnlyList<string> Warnings);
}
