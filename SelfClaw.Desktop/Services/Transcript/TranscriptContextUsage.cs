namespace SelfClaw.Desktop.Services;

public sealed record TranscriptContextUsage(
    long UsedTokens,
    int ContextWindowTokens,
    int AutoCompactTokenLimit,
    bool IsMeasured);
