using System.Globalization;
using System.IO;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services;

internal sealed class SubagentDefinitionCatalog
{
    internal const int DefaultMaxRunSeconds = 900;
    internal const string DefaultToolPolicy = "read-only";

    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "name",
        "description",
        "modelProfileId",
        "tools",
        "plugins",
        "skills",
        "mcpServers",
        "maxRunSeconds"
    };
    private readonly string _subagentsDirectory;
    private readonly AgentMarkdownDocumentParser _documentParser = new();
    private readonly object _syncRoot = new();

    public SubagentDefinitionCatalog(StoragePaths storagePaths)
    {
        _subagentsDirectory = Path.Combine(storagePaths.AppDataDirectory, "subagents");
    }

    internal string SubagentsDirectory => _subagentsDirectory;

    internal IReadOnlyList<SubagentDefinition> LoadAll()
    {
        lock (_syncRoot)
        {
            Directory.CreateDirectory(_subagentsDirectory);
            return Directory
                .EnumerateFiles(_subagentsDirectory, "*.md", SearchOption.TopDirectoryOnly)
                .Select(LoadFile)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    internal SubagentDefinition? Get(string subagentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subagentId);
        var normalizedId = NormalizeDefinitionId(subagentId);
        if (!IsValidDefinitionId(normalizedId))
        {
            return null;
        }

        lock (_syncRoot)
        {
            var filePath = Path.Combine(_subagentsDirectory, $"{normalizedId}.md");
            return File.Exists(filePath) ? LoadFile(filePath) : null;
        }
    }

    private SubagentDefinition LoadFile(string filePath)
    {
        var definitionId = NormalizeDefinitionId(Path.GetFileNameWithoutExtension(filePath));
        if (!IsValidDefinitionId(definitionId))
        {
            return InvalidDefinition(definitionId, filePath, "Subagent file name is invalid.");
        }

        try
        {
            var document = _documentParser.Parse(File.ReadAllText(filePath));
            return MapDefinition(definitionId, filePath, document);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return InvalidDefinition(definitionId, filePath, $"Unable to load subagent file: {exception.Message}");
        }
    }

    private static SubagentDefinition MapDefinition(
        string definitionId,
        string filePath,
        MarkdownDefinitionDocument document)
    {
        var diagnostics = new List<string>(document.Diagnostics);
        ValidateKnownFields(document, diagnostics);

        var name = ReadRequiredScalar(document, "name", diagnostics);
        var description = ReadRequiredScalar(document, "description", diagnostics);
        var modelProfileId = ParseModelProfileId(document, diagnostics);
        var toolPolicy = ParseToolPolicy(document, diagnostics);
        var pluginIds = ReadIdentifierList(document, "plugins", NormalizeExtensionId, diagnostics);
        var skillIds = ReadIdentifierList(document, "skills", NormalizeSkillId, diagnostics);
        var mcpServerIds = ReadIdentifierList(document, "mcpServers", NormalizeExtensionId, diagnostics);
        var maxRunSeconds = ParseMaxRunSeconds(document, diagnostics);
        var instructions = NormalizeInstructions(document.Body);
        if (instructions.Length == 0)
        {
            diagnostics.Add("Subagent instructions are required.");
        }

        return new SubagentDefinition(
            definitionId,
            name,
            description,
            modelProfileId,
            toolPolicy,
            pluginIds,
            skillIds,
            mcpServerIds,
            maxRunSeconds,
            instructions,
            filePath,
            diagnostics.Count == 0,
            diagnostics);
    }

    private static void ValidateKnownFields(
        MarkdownDefinitionDocument document,
        ICollection<string> diagnostics)
    {
        foreach (var field in document.Scalars.Keys.Concat(document.Lists.Keys))
        {
            if (!AllowedFields.Contains(field))
            {
                diagnostics.Add($"Unsupported subagent front matter field '{field}'.");
            }
        }
    }

    private static string ReadRequiredScalar(
        MarkdownDefinitionDocument document,
        string field,
        ICollection<string> diagnostics)
    {
        if (document.Lists.ContainsKey(field))
        {
            diagnostics.Add($"Subagent field '{field}' must be a scalar.");
            return string.Empty;
        }

        if (!document.Scalars.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add($"Subagent field '{field}' is required.");
            return string.Empty;
        }

        return value.Trim();
    }

    private static Guid? ParseModelProfileId(
        MarkdownDefinitionDocument document,
        ICollection<string> diagnostics)
    {
        if (document.Lists.ContainsKey("modelProfileId"))
        {
            diagnostics.Add("Subagent field 'modelProfileId' must be a scalar.");
            return null;
        }

        if (!document.Scalars.TryGetValue("modelProfileId", out var value))
        {
            return null;
        }

        if (Guid.TryParse(value, out var modelProfileId) && modelProfileId != Guid.Empty)
        {
            return modelProfileId;
        }

        diagnostics.Add($"Subagent modelProfileId '{value}' is invalid.");
        return null;
    }

    private static string ParseToolPolicy(
        MarkdownDefinitionDocument document,
        ICollection<string> diagnostics)
    {
        if (document.Lists.ContainsKey("tools"))
        {
            diagnostics.Add("Subagent field 'tools' must be a scalar.");
            return DefaultToolPolicy;
        }

        if (!document.Scalars.TryGetValue("tools", out var value))
        {
            return DefaultToolPolicy;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "none" or DefaultToolPolicy or "system")
        {
            return normalized;
        }

        diagnostics.Add($"Subagent tools value '{value}' is invalid.");
        return DefaultToolPolicy;
    }

    private static int ParseMaxRunSeconds(
        MarkdownDefinitionDocument document,
        ICollection<string> diagnostics)
    {
        if (document.Lists.ContainsKey("maxRunSeconds"))
        {
            diagnostics.Add("Subagent field 'maxRunSeconds' must be a scalar.");
            return DefaultMaxRunSeconds;
        }

        if (!document.Scalars.TryGetValue("maxRunSeconds", out var value))
        {
            return DefaultMaxRunSeconds;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 30 and <= 3600)
        {
            return seconds;
        }

        diagnostics.Add($"Subagent maxRunSeconds '{value}' must be between 30 and 3600.");
        return DefaultMaxRunSeconds;
    }

    private static IReadOnlyList<string> ReadIdentifierList(
        MarkdownDefinitionDocument document,
        string field,
        Func<string?, string> normalize,
        ICollection<string> diagnostics)
    {
        if (document.Scalars.ContainsKey(field))
        {
            diagnostics.Add($"Subagent field '{field}' must be a list.");
            return [];
        }

        if (!document.Lists.TryGetValue(field, out var values))
        {
            return [];
        }

        return values
            .Select(normalize)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SubagentDefinition InvalidDefinition(string definitionId, string filePath, string diagnostic)
        => new(
            definitionId,
            definitionId,
            string.Empty,
            null,
            DefaultToolPolicy,
            [],
            [],
            [],
            DefaultMaxRunSeconds,
            string.Empty,
            filePath,
            false,
            [diagnostic]);

    private static string NormalizeDefinitionId(string? definitionId)
        => definitionId?.Trim().ToLowerInvariant() ?? string.Empty;

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

    private static string NormalizeInstructions(string? instructions)
        => string.IsNullOrWhiteSpace(instructions)
            ? string.Empty
            : instructions.ReplaceLineEndings("\n").Trim();

    private static bool IsValidDefinitionId(string definitionId)
        => definitionId.Length > 0 &&
           definitionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
