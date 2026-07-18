namespace SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

public sealed record ProgrammingAssistantSettings
{
    public bool HasScanned { get; init; }

    public DateTimeOffset? ScannedAtUtc { get; init; }

    public string? SelectedCliId { get; init; }

    /// <summary>
    /// The model the user picked for <see cref="SelectedCliId"/> in the composer, or <c>null</c> to let the
    /// CLI use its own configured default. Persisted so the choice survives a restart; reset to <c>null</c>
    /// whenever the selected CLI changes (its catalogue no longer applies).
    /// </summary>
    public string? SelectedModel { get; init; }

    /// <summary>
    /// The reasoning effort the user picked for <see cref="SelectedCliId"/> (only Codex advertises these), or
    /// <c>null</c> to defer to the CLI's own default. Persisted and reset alongside <see cref="SelectedModel"/>.
    /// </summary>
    public string? SelectedReasoningLevel { get; init; }

    public IReadOnlyList<DetectedProgrammingCli> Tools { get; init; } = [];
}
