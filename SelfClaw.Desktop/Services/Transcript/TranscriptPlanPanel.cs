namespace SelfClaw.Desktop.Services;

public sealed record TranscriptPlanPanel(
    bool IsVisible,
    string State,
    string Title,
    string? Summary,
    string StatusText,
    IReadOnlyList<TranscriptPlanStep> Steps)
{
    public static TranscriptPlanPanel Hidden { get; } = new(
        false,
        "idle",
        "计划模式",
        null,
        string.Empty,
        []);
}
