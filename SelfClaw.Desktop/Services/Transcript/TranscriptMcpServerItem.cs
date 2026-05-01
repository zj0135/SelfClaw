namespace SelfClaw.Desktop.Services;

public sealed record TranscriptMcpServerItem(
    string Id,
    string DisplayName,
    bool Enabled,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env);
