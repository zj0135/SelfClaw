using System.Text.Json;

namespace SelfClaw.Infrastructure.Agents.Cli.Discovery;

/// <summary>
/// Pure parsers that turn a CLI's "list models" command output into an ordered, de-duplicated set of
/// model identifiers. Each CLI exposes its catalogue differently, so the detection scan
/// (<c>ProgrammingAssistantSettingsService</c>) pairs the right command with the matching parser here.
/// <list type="bullet">
///   <item>OpenCode — <c>opencode models</c> prints one <c>provider/model</c> slug per line.</item>
///   <item>Codex — <c>codex debug models</c> prints a JSON document whose <c>models</c> array carries a
///         <c>slug</c>, a <c>visibility</c> ("list"/"hide") and a <c>priority</c> ordering hint.</item>
///   <item>Claude Code has no discovery command; its list is maintained by hand and never reaches here.</item>
/// </list>
/// Every parser is defensive: malformed, empty, or unexpected output yields an empty list so the caller
/// can fall back to the CLI's own default configuration rather than surfacing an error.
/// </summary>
public static class CliModelListParser
{
    /// <summary>
    /// Parses the newline-delimited output of <c>opencode models</c>. Keeps only lines shaped like a
    /// <c>provider/model</c> slug (contains a slash, no interior whitespace), which excludes any progress
    /// or log noise the CLI may interleave. Order and case are preserved; exact duplicates are dropped.
    /// </summary>
    public static IReadOnlyList<string> ParseOpenCodeModels(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        var models = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.Contains('/') || line.Any(char.IsWhiteSpace))
            {
                continue;
            }

            if (seen.Add(line))
            {
                models.Add(line);
            }
        }

        return models;
    }

    /// <summary>
    /// Parses the JSON emitted by <c>codex debug models</c>. Selects the models Codex itself would surface
    /// in its picker (<c>visibility == "list"</c>), ordered by the CLI's <c>priority</c> hint (ascending),
    /// and returns their <c>slug</c> values — the identifier accepted by <c>codex --model</c>.
    /// </summary>
    public static IReadOnlyList<string> ParseCodexDebugModels(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            return models
                .EnumerateArray()
                .Where(model => model.ValueKind == JsonValueKind.Object)
                .Select(model => new
                {
                    Slug = ReadString(model, "slug"),
                    Visibility = ReadString(model, "visibility"),
                    Priority = ReadInt(model, "priority"),
                })
                .Where(model =>
                    !string.IsNullOrWhiteSpace(model.Slug) &&
                    string.Equals(model.Visibility, "list", StringComparison.OrdinalIgnoreCase))
                .OrderBy(model => model.Priority)
                .Select(model => model.Slug!)
                .Where(seen.Add)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Extracts the reasoning effort levels Codex supports from the <c>codex debug models</c> JSON. Codex
    /// reports them per model in <c>supported_reasoning_levels[].effort</c>; this returns their union across
    /// the visible (<c>visibility == "list"</c>) models, preserving the CLI's own ordering (e.g.
    /// <c>low, medium, high, xhigh</c>) and dropping duplicates. Empty when the output is unusable.
    /// </summary>
    public static IReadOnlyList<string> ParseCodexReasoningLevels(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var levels = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(model, "visibility"), "list", StringComparison.OrdinalIgnoreCase) ||
                    !model.TryGetProperty("supported_reasoning_levels", out var supported) ||
                    supported.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var level in supported.EnumerateArray())
                {
                    var effort = level.ValueKind == JsonValueKind.Object ? ReadString(level, "effort") : null;
                    if (!string.IsNullOrWhiteSpace(effort) && seen.Add(effort))
                    {
                        levels.Add(effort);
                    }
                }
            }

            return levels;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : int.MaxValue;
}
