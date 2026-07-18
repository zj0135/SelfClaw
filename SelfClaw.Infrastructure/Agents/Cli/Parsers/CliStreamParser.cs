using System.Text;
using System.Text.Json;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Parsers;

/// <summary>
/// Base for the CLI stdout parsers. Owns the shared per-line JSON envelope — trim, skip blank lines,
/// parse, and surface anything that is not a JSON object verbatim as a <see cref="RawOutputEvent"/> for
/// diagnostics (plan.md §5, T3.3) — and delegates the recognised events to <see cref="HandleObject"/>.
/// <para>
/// A parser is stateful and single-use per run: subclasses track session id, block ids and tool-call ids
/// across the stream, so create a fresh instance for each turn. Callers feed one already-split stdout line
/// at a time via <see cref="ParseLine"/>; there is no internal buffering because the process session yields
/// complete newline-delimited lines.
/// </para>
/// </summary>
public abstract class CliStreamParser
{
    /// <summary>
    /// Parses a single stdout line into the events it encodes. A blank line yields nothing; a line that is
    /// not a JSON object is returned verbatim as a <see cref="RawOutputEvent"/>.
    /// </summary>
    public IEnumerable<AgentStreamEvent> ParseLine(string rawLine)
    {
        var line = rawLine.Trim('\r', ' ', '\t');
        if (line.Length == 0)
            return Array.Empty<AgentStreamEvent>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new AgentStreamEvent[] { new RawOutputEvent(line) };
        }

        using (doc)
        {
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                ? HandleObject(root)
                : new AgentStreamEvent[] { new RawOutputEvent(line) };
        }
    }

    /// <summary>Maps one parsed JSON object line onto its events. Unknown-but-valid types return nothing.</summary>
    protected abstract IEnumerable<AgentStreamEvent> HandleObject(JsonElement root);

    /// <summary>Builds a single-line, length-capped summary of a tool result for the transcript.</summary>
    protected static string? BuildSummary(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        const int maxLength = 120;
        var firstLine = content.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0)
            firstLine = firstLine[..newline];

        var summary = firstLine.Trim().ToString();
        return summary.Length > maxLength ? summary[..maxLength] + "…" : summary;
    }

    /// <summary>Reads a string property from a JSON object, or <c>null</c> when absent or not a string.</summary>
    protected static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads an int property from a JSON object, or <c>null</c> when absent or not an integer.</summary>
    protected static int? GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
