namespace SelfClaw.Core.Models;

/// <summary>
/// One lifetime or one range-window aggregate. Token fields are raw counts — the renderer owns
/// abbreviation. <see cref="CacheHitRate"/> is <c>cached / (cached + uncached)</c>, null when nothing
/// was reported; <see cref="ToolErrorRate"/> is null when the window has no tool calls.
/// </summary>
public sealed record UsageSummary(
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    double? CacheHitRate,
    int TotalTurns,
    int TotalSessions,
    int ActiveDays,
    int CurrentStreakDays,
    int LongestStreakDays,
    long LongestTurnMs,
    long PeakDayTokens,
    DateOnly? PeakDayDate,
    UsageFavoriteModel? FavoriteModel,
    int ToolCallCount,
    double? ToolErrorRate,
    DateOnly? FirstActivityDate,
    DateOnly? LastActivityDate);
