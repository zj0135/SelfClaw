using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Transcript.Views;

public sealed record TranscriptTurnOutcome(
    string TurnId,
    string Status,
    string? ErrorMessage,
    long? DurationMs,
    TurnUsage? Usage);
