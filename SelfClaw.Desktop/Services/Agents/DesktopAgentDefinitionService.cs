using System.IO;
using System.Text;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopAgentDefinitionService
{
    public const string BuildAgentId = "build";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly string _agentsDirectory;
    private readonly object _syncRoot = new();

    public DesktopAgentDefinitionService(StoragePaths storagePaths)
    {
        _agentsDirectory = Path.Combine(storagePaths.AppDataDirectory, "agents");
    }

    public string AgentsDirectory => _agentsDirectory;

    public IReadOnlyList<DesktopAgentDefinition> LoadAll()
    {
        lock (_syncRoot)
        {
            EnsureSystemAgents();
            return LoadAllCore();
        }
    }

    public DesktopAgentDefinition Save(DesktopAgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var agentId = NormalizeAgentId(definition.Id);
        if (!IsValidAgentId(agentId))
        {
            throw new ArgumentException("Agent id is invalid.", nameof(definition));
        }

        lock (_syncRoot)
        {
            var saved = NormalizeDefinition(definition with
            {
                Id = agentId,
                FilePath = GetAgentFilePath(agentId),
                IsBuiltIn = IsBuiltInAgentId(agentId),
                Warnings = []
            });
            WriteAgentFile(saved);
            return saved;
        }
    }

    public DesktopAgentDefinition SetExtensionBinding(
        string agentId,
        ExtensionItemKey key,
        bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Id);

        lock (_syncRoot)
        {
            EnsureSystemAgents();
            var normalizedAgentId = NormalizeAgentId(agentId);
            var definition = LoadAllCore().FirstOrDefault(item =>
                string.Equals(item.Id, normalizedAgentId, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                throw new KeyNotFoundException($"Agent '{normalizedAgentId}' was not found.");
            }

            var updated = key.Kind switch
            {
                ExtensionKind.Plugin => definition with
                {
                    PluginIds = SetListItem(definition.PluginIds, key.Id, enabled)
                },
                ExtensionKind.Skill => definition with
                {
                    SkillIds = SetListItem(definition.SkillIds, key.Id, enabled)
                },
                ExtensionKind.McpServer => definition with
                {
                    McpServerIds = SetListItem(definition.McpServerIds, key.Id, enabled)
                },
                _ => throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "Unsupported extension kind.")
            };
            WriteAgentFile(updated);
            return updated;
        }
    }

    private IReadOnlyList<DesktopAgentDefinition> LoadAllCore()
    {
        var agents = new List<DesktopAgentDefinition>();
        foreach (var filePath in Directory.EnumerateFiles(_agentsDirectory, "*.md", SearchOption.TopDirectoryOnly))
        {
            try
            {
                agents.Add(LoadAgentFromFile(filePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                var rawId = Path.GetFileNameWithoutExtension(filePath);
                agents.Add(CreateInvalidDefinition(rawId, filePath, exception.Message));
            }
        }

        return agents
            .OrderBy(item => GetAgentSortOrder(item.Id))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private DesktopAgentDefinition LoadAgentFromFile(string filePath)
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
            return CreateInvalidDefinition(agentId, filePath, exception.Message);
        }

        var parsed = ParseAgentMarkdown(agentId, markdown);
        return new DesktopAgentDefinition(
            agentId,
            parsed.Name,
            parsed.Description,
            parsed.Mode,
            parsed.ToolPolicy,
            parsed.PluginIds,
            ExceptDisabled(parsed.SkillIds, parsed.DisabledSkillIds),
            ExceptDisabled(parsed.McpServerIds, parsed.DisabledMcpServerIds),
            parsed.Instructions,
            filePath,
            IsBuiltInAgentId(agentId),
            parsed.Warnings);
    }

    private void EnsureSystemAgents()
    {
        Directory.CreateDirectory(_agentsDirectory);
        var filePath = GetAgentFilePath(BuildAgentId);
        if (File.Exists(filePath))
        {
            return;
        }

        WriteAgentFile(new DesktopAgentDefinition(
            BuildAgentId,
            BuildAgentId,
            "通用代理（默认）",
            AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            """
            You are the default build agent for SelfClaw.
            Work directly in the selected workspace, keep changes scoped, and verify important outcomes before you conclude.
            """,
            filePath,
            true,
            []));
    }

    private void WriteAgentFile(DesktopAgentDefinition definition)
    {
        Directory.CreateDirectory(_agentsDirectory);
        var destinationPath = GetAgentFilePath(definition.Id);
        var temporaryPath = Path.Combine(
            _agentsDirectory,
            $".{definition.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, SerializeAgentMarkdown(definition), Utf8WithoutBom);
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string SerializeAgentMarkdown(DesktopAgentDefinition definition)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"name: {EscapeScalar(definition.Name)}");
        builder.AppendLine($"description: {EscapeScalar(definition.Description)}");
        builder.AppendLine($"mode: {ToModeId(definition.Mode)}");
        builder.AppendLine($"tools: {NormalizeToolPolicy(definition.ToolPolicy)}");
        AppendList(builder, "plugins", definition.PluginIds);
        AppendList(builder, "skills", definition.SkillIds);
        AppendList(builder, "mcpServers", definition.McpServerIds);
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append(NormalizeInstructions(definition.Instructions));
        builder.AppendLine();
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{key}:");
        foreach (var value in values)
        {
            builder.AppendLine($"  - {value}");
        }
    }

    private static AgentParseResult ParseAgentMarkdown(string agentId, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return CreateEmptyParseResult(agentId, string.Empty, "Agent file is empty; using default values.");
        }

        var normalized = markdown.ReplaceLineEndings("\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return CreateEmptyParseResult(agentId, normalized.Trim(), "Front matter is missing; using default metadata.");
        }

        var endIndex = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return CreateEmptyParseResult(agentId, normalized.Trim(), "Front matter is incomplete; using default metadata.");
        }

        var metadataBlock = normalized[4..endIndex];
        var instructions = normalized[(endIndex + 5)..].Trim();
        var warnings = new List<string>();
        var name = agentId;
        var description = string.Empty;
        var mode = AgentExecutionMode.Direct;
        var toolPolicy = AgentRuntimeDefinition.SystemToolPolicy;
        var pluginIds = new List<string>();
        var skillIds = new List<string>();
        var disabledSkillIds = new List<string>();
        var mcpServerIds = new List<string>();
        var disabledMcpServerIds = new List<string>();
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
                AddListItem(
                    currentList,
                    Unquote(trimmed[2..].Trim()),
                    pluginIds,
                    skillIds,
                    disabledSkillIds,
                    mcpServerIds,
                    disabledMcpServerIds,
                    warnings);
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
                    toolPolicy = ParseToolPolicy(value, warnings);
                    break;
                case "plugins":
                case "skills":
                case "disabledSkills":
                case "mcpServers":
                case "disabledMcpServers":
                    currentList = key;
                    break;
                default:
                    warnings.Add($"Ignoring unsupported front matter key '{key}'.");
                    break;
            }
        }

        return new AgentParseResult(
            string.IsNullOrWhiteSpace(name) ? agentId : name.Trim(),
            description.Trim(),
            mode,
            toolPolicy,
            NormalizeIdentifiers(pluginIds, NormalizeExtensionId),
            NormalizeIdentifiers(skillIds, NormalizeSkillId),
            NormalizeIdentifiers(disabledSkillIds, NormalizeSkillId),
            NormalizeIdentifiers(mcpServerIds, NormalizeExtensionId),
            NormalizeIdentifiers(disabledMcpServerIds, NormalizeExtensionId),
            NormalizeInstructions(instructions),
            warnings);
    }

    private static void AddListItem(
        string? currentList,
        string value,
        ICollection<string> pluginIds,
        ICollection<string> skillIds,
        ICollection<string> disabledSkillIds,
        ICollection<string> mcpServerIds,
        ICollection<string> disabledMcpServerIds,
        ICollection<string> warnings)
    {
        var target = currentList switch
        {
            "plugins" => pluginIds,
            "skills" => skillIds,
            "disabledSkills" => disabledSkillIds,
            "mcpServers" => mcpServerIds,
            "disabledMcpServers" => disabledMcpServerIds,
            _ => null
        };
        if (target is null)
        {
            warnings.Add($"Ignoring list item '- {value}' because it is not attached to a known key.");
            return;
        }

        target.Add(value);
    }

    private static string ParseToolPolicy(string value, ICollection<string> warnings)
    {
        var toolPolicy = NormalizeToolPolicy(value);
        if (string.Equals(toolPolicy, AgentRuntimeDefinition.SystemToolPolicy, StringComparison.OrdinalIgnoreCase))
        {
            return AgentRuntimeDefinition.SystemToolPolicy;
        }

        warnings.Add($"Unsupported tools value '{value}'. Using '{AgentRuntimeDefinition.SystemToolPolicy}'.");
        return AgentRuntimeDefinition.SystemToolPolicy;
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

    private static DesktopAgentDefinition NormalizeDefinition(DesktopAgentDefinition definition)
        => definition with
        {
            Name = string.IsNullOrWhiteSpace(definition.Name) ? definition.Id : definition.Name.Trim(),
            Description = definition.Description.Trim(),
            ToolPolicy = NormalizeToolPolicy(definition.ToolPolicy),
            PluginIds = NormalizeIdentifiers(definition.PluginIds, NormalizeExtensionId),
            SkillIds = NormalizeIdentifiers(definition.SkillIds, NormalizeSkillId),
            McpServerIds = NormalizeIdentifiers(definition.McpServerIds, NormalizeExtensionId),
            Instructions = NormalizeInstructions(definition.Instructions)
        };

    private static IReadOnlyList<string> SetListItem(IReadOnlyList<string> values, string id, bool enabled)
    {
        var results = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabled)
        {
            results.Add(id.Trim());
        }
        else
        {
            results.Remove(id.Trim());
        }

        return results.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> ExceptDisabled(
        IReadOnlyList<string> selected,
        IReadOnlyList<string> disabled)
    {
        var disabledSet = disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selected.Where(item => !disabledSet.Contains(item)).ToArray();
    }

    private static IReadOnlyList<string> NormalizeIdentifiers(
        IEnumerable<string> values,
        Func<string?, string> normalize)
        => values
            .Select(normalize)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static DesktopAgentDefinition CreateInvalidDefinition(
        string? rawId,
        string filePath,
        string error)
    {
        var agentId = string.IsNullOrWhiteSpace(rawId) ? "invalid-agent" : rawId;
        return new DesktopAgentDefinition(
            agentId,
            agentId,
            string.Empty,
            AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            string.Empty,
            filePath,
            false,
            [$"Unable to load agent file: {error}"]);
    }

    private static AgentParseResult CreateEmptyParseResult(
        string agentId,
        string instructions,
        string warning)
        => new(
            agentId,
            string.Empty,
            AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            [],
            [],
            instructions,
            [warning]);

    private string GetAgentFilePath(string agentId)
        => Path.Combine(_agentsDirectory, $"{agentId}.md");

    private static string EscapeScalar(string? value)
        => $"\"{(value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

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

    private static bool IsBuiltInAgentId(string agentId)
        => string.Equals(agentId, BuildAgentId, StringComparison.OrdinalIgnoreCase);

    private static int GetAgentSortOrder(string agentId)
        => IsBuiltInAgentId(agentId) ? 0 : 2;

    private static string NormalizeAgentId(string? agentId)
        => agentId?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeExtensionId(string? extensionId)
        => extensionId?.Trim() ?? string.Empty;

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

}
