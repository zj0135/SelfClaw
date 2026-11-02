namespace SelfClaw.Core.Models;

/// <summary>
/// One workspace text search: the matches that fit the result budget plus, when the search stopped early,
/// why it stopped. Both flags exist because the model has to know that more matches are there — a silently
/// shortened hit list reads as "the repository has nothing else", which is a wrong answer it will act on.
/// </summary>
public sealed record WorkspaceSearchResult(
    IReadOnlyList<WorkspaceSearchHit> Hits,
    bool MatchLimitReached = false,
    bool SizeLimitReached = false);
