namespace SelfClaw.Desktop.Services;

public sealed record McpServerEditorResult(
    string ServerId,
    string DisplayName,
    bool Enabled,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env);
