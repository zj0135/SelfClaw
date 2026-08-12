namespace SelfClaw.Core.Models;

public sealed record GitBranchInfo(
    string Name,
    string FullName,
    string CommitSha,
    string? UpstreamName,
    bool IsRemote,
    bool IsCurrent,
    string? CheckoutPath);
