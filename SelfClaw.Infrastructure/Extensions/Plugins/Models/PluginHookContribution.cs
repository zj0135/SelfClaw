namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record PluginHookContribution(
    string Id,
    PluginHookEvent Event,
    PluginHookMatcher Matcher,
    string Command,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    PluginHookFailurePolicy OnFailure,
    bool RunAsync,
    bool IncludeRequestBody);
