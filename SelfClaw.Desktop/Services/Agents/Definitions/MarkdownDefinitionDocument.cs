namespace SelfClaw.Desktop.Services;

internal sealed record MarkdownDefinitionDocument(
    IReadOnlyDictionary<string, string> Scalars,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Lists,
    string Body,
    IReadOnlyList<string> Diagnostics);
