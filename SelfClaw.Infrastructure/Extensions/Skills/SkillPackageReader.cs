using System.Text;
using System.Text.RegularExpressions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Skills;

internal sealed partial class SkillPackageReader
{
    private const string DefaultVersion = "0.0.0";
    private const string AllowedToolsKey = "allowed-tools";

    private static readonly HashSet<string> KnownScalarKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "version", "license", "compatibility"
    };

    private readonly ExtensionPackageLimits _limits;

    public SkillPackageReader(ExtensionPackageLimits limits)
    {
        _limits = limits;
    }

    public async Task<SkillPackageMetadata> ReadAsync(
        string skillFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillFilePath);
        var file = new FileInfo(skillFilePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("SKILL.md was not found.", skillFilePath);
        }

        if (file.Length > _limits.MaximumManifestBytes)
        {
            throw new InvalidDataException($"SKILL.md exceeds the {_limits.MaximumManifestBytes} byte limit.");
        }

        string markdown;
        await using (var stream = new FileStream(
                         file.FullName,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
        {
            markdown = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var match = FrontMatterPattern().Match(markdown);
        if (!match.Success)
        {
            throw new InvalidDataException("SKILL.md must start with a YAML front matter block.");
        }

        var values = ParseFrontMatter(match.Groups["frontMatter"].Value);
        var name = GetRequiredScalar(values, "name");
        var description = GetRequiredScalar(values, "description");
        var id = NormalizeSkillId(name);
        var version = values.Scalars.GetValueOrDefault("version")?.Trim();
        return new SkillPackageMetadata(
            id,
            name.Trim(),
            description.Trim(),
            string.IsNullOrWhiteSpace(version) ? DefaultVersion : Unquote(version),
            values.Triggers,
            markdown,
            values.License,
            values.Compatibility,
            values.AllowedTools,
            values.Metadata,
            values.Warnings);
    }

    public static string NormalizeSkillId(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        var normalized = Unquote(skillId.Trim()).Replace('\\', '/').Trim('/').ToLowerInvariant();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment =>
                segment is "." or ".." ||
                segment.Length > 64 ||
                segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
        {
            throw new InvalidDataException("Skill name must be a lowercase path of ASCII letters, digits, '-' or '_'.");
        }

        var id = string.Join('/', segments);
        if (id.Length > 256)
        {
            throw new InvalidDataException("Skill name exceeds the 256 character limit.");
        }

        return id;
    }

    private static ParsedFrontMatter ParseFrontMatter(string frontMatter)
    {
        var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var triggers = new List<string>();
        var allowedTools = new List<string>();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var lines = frontMatter.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("triggers", StringComparison.OrdinalIgnoreCase))
            {
                ParseTriggers(lines, ref index, value, triggers);
                continue;
            }

            if (key.Equals(AllowedToolsKey, StringComparison.OrdinalIgnoreCase))
            {
                ParseAllowedTools(lines, ref index, value, allowedTools);
                continue;
            }

            if (key.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            {
                ParseMetadata(lines, ref index, value, metadata, warnings);
                continue;
            }

            if (value is ">" or ">-" or "|" or "|-")
            {
                value = ReadBlockScalar(lines, ref index, value.StartsWith('>'));
            }

            scalars[key] = Unquote(value);
            if (!KnownScalarKeys.Contains(key))
            {
                warnings.Add($"Unrecognized front matter field '{key}' was ignored.");
            }
        }

        return new ParsedFrontMatter(
            scalars,
            triggers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            scalars.GetValueOrDefault("license"),
            scalars.GetValueOrDefault("compatibility"),
            allowedTools.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            metadata,
            warnings);
    }

    /// <summary>
    /// Accepts the specification's space-delimited form, the comma-delimited form that is common in the
    /// wild, and the inline <c>[a, b]</c> and block-sequence shapes.
    /// </summary>
    private static void ParseAllowedTools(
        IReadOnlyList<string> lines,
        ref int index,
        string value,
        ICollection<string> allowedTools)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var items = value.StartsWith('[') && value.EndsWith(']')
                ? value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : value.Split(
                    [',', ' ', '\t'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var item in items)
            {
                AddEntry(allowedTools, item);
            }

            return;
        }

        while (index + 1 < lines.Count)
        {
            var next = lines[index + 1];
            var trimmed = next.TrimStart();
            if (!char.IsWhiteSpace(next.FirstOrDefault()) || !trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            index++;
            AddEntry(allowedTools, trimmed[2..]);
        }
    }

    private static void ParseMetadata(
        IReadOnlyList<string> lines,
        ref int index,
        string value,
        IDictionary<string, string> metadata,
        ICollection<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            warnings.Add("Front matter field 'metadata' must be a block mapping; the inline value was ignored.");
            return;
        }

        while (index + 1 < lines.Count)
        {
            var next = lines[index + 1];
            var trimmed = next.TrimStart();
            if (!char.IsWhiteSpace(next.FirstOrDefault()) || trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                break;
            }

            index++;
            metadata[trimmed[..separator].Trim()] = Unquote(trimmed[(separator + 1)..].Trim());
        }
    }

    private static void AddEntry(ICollection<string> target, string value)
    {
        var entry = Unquote(value.Trim());
        if (!string.IsNullOrWhiteSpace(entry))
        {
            target.Add(entry);
        }
    }

    private static void ParseTriggers(
        IReadOnlyList<string> lines,
        ref int index,
        string value,
        ICollection<string> triggers)
    {
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            foreach (var item in value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddEntry(triggers, item);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            AddEntry(triggers, value);
            return;
        }

        while (index + 1 < lines.Count)
        {
            var next = lines[index + 1];
            var trimmed = next.TrimStart();
            if (!char.IsWhiteSpace(next.FirstOrDefault()) || !trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            index++;
            AddEntry(triggers, trimmed[2..]);
        }
    }

    private static string ReadBlockScalar(IReadOnlyList<string> lines, ref int index, bool fold)
    {
        var values = new List<string>();
        while (index + 1 < lines.Count &&
               (string.IsNullOrWhiteSpace(lines[index + 1]) || char.IsWhiteSpace(lines[index + 1][0])))
        {
            index++;
            values.Add(lines[index].Trim());
        }

        return string.Join(fold ? " " : "\n", values).Trim();
    }

    private static string GetRequiredScalar(ParsedFrontMatter values, string key)
    {
        var value = values.Scalars.GetValueOrDefault(key);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"SKILL.md front matter requires '{key}'.")
            : value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return value;
    }

    [GeneratedRegex(@"\A---[ \t]*\r?\n(?<frontMatter>.*?)^---[ \t]*\r?\n?", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FrontMatterPattern();

    private sealed record ParsedFrontMatter(
        IReadOnlyDictionary<string, string> Scalars,
        IReadOnlyList<string> Triggers,
        string? License,
        string? Compatibility,
        IReadOnlyList<string> AllowedTools,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<string> Warnings);
}
