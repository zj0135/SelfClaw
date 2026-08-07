namespace SelfClaw.Desktop.Services;

internal sealed class AgentMarkdownDocumentParser
{
    internal MarkdownDefinitionDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Empty(string.Empty, "Definition file is empty.");
        }

        var normalized = markdown.ReplaceLineEndings("\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return Empty(normalized.Trim(), "Front matter is missing.");
        }

        var endIndex = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return Empty(normalized.Trim(), "Front matter is incomplete.");
        }

        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        var metadataBlock = normalized[4..endIndex];
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
                    diagnostics.Add($"Ignoring list item '{trimmed}' because it is not attached to a field.");
                }
                else
                {
                    lists[currentList].Add(Unquote(trimmed[2..].Trim()));
                }

                continue;
            }

            currentList = null;
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                diagnostics.Add($"Ignoring malformed front matter line '{line}'.");
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (scalars.ContainsKey(key) || lists.ContainsKey(key))
            {
                diagnostics.Add($"Front matter field '{key}' is duplicated.");
                continue;
            }

            if (value.Length == 0)
            {
                lists[key] = [];
                currentList = key;
                continue;
            }

            scalars[key] = Unquote(value);
        }

        return new MarkdownDefinitionDocument(
            scalars,
            lists.ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<string>)item.Value.ToArray(),
                StringComparer.Ordinal),
            normalized[(endIndex + 5)..].Trim(),
            diagnostics);
    }

    private static MarkdownDefinitionDocument Empty(string body, string diagnostic)
        => new(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            body,
            [diagnostic]);

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
