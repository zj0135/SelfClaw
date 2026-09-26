namespace SelfClaw.Core.Models;

public sealed record PluginHookView(
    string Id,
    string Event,
    string MatcherSummary,
    string CommandLine,
    int TimeoutSeconds,
    string? OnFailure,
    bool IsAsync,
    bool IncludeRequestBody);
