namespace SelfClaw.Desktop.Services;

public sealed record TranscriptSkillItem(
    string Id,
    string Name,
    string RelativePath,
    string SkillFilePath,
    string Markdown,
    bool Enabled);
