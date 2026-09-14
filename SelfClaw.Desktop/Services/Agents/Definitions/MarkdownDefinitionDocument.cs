namespace SelfClaw.Desktop.Services.Agents.Definitions;

internal sealed record MarkdownDefinitionDocument(
    IReadOnlyDictionary<string, string> Scalars,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Lists,
    string Body,
    IReadOnlyList<string> Diagnostics);
