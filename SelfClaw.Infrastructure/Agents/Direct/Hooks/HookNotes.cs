using System.Text;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// The host-generated texts hooks produce. Live turns and prompt replay both call these, so a tool
/// card's "arguments were modified" line and the line the model sees on replay cannot drift.
/// </summary>
internal static class HookNotes
{
    internal const int MaximumArgumentsJsonBytes = 2 * 1024;
    internal const string NoReason = "No reason given.";

    internal static string Describe(HookSource source) => $"{source.PluginId}/{source.HookId}";

    internal static string ArgumentsModified(string effectiveArgumentsJson, IReadOnlyList<HookSource> modifiers)
    {
        var (json, _) = HookProtocol.Truncate(effectiveArgumentsJson, MaximumArgumentsJsonBytes);
        var subject = modifiers.Count == 1
            ? $"hook '{Describe(modifiers[0])}'"
            : $"hooks {string.Join(", ", modifiers.Select(source => $"'{Describe(source)}'"))}";
        return $"Arguments were modified by {subject} before execution: {json}";
    }

    internal static string ContextAdded(HookSource source, int characters)
        => $"Hook '{Describe(source)}' added context ({characters} chars).";

    internal static string FailureIgnored(HookSource source, string kind)
        => $"Hook '{Describe(source)}' failed ({kind}); ignored.";

    internal static string ContextDropped(HookSource source)
        => $"Hook '{Describe(source)}' context was dropped because the turn context limit (64 KiB) was reached.";

    internal static string PluginSkipped(string pluginId, string reason)
        => $"Plugin '{pluginId}' declares hooks but was skipped ({reason}); its hooks are not active in this turn.";

    internal static string InheritedHookPluginBlocked(string pluginId)
        => $"Inherited hook plugin '{pluginId}' is unavailable or changed since delegation; the turn was blocked.";

    internal static string Blocked(HookSource source, string? reason)
        => $"Blocked by hook '{Describe(source)}': {reason ?? NoReason}";

    internal static string BlockedByFailure(HookSource source, string kind, string? detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";
        return $"Hook '{Describe(source)}' failed ({kind}) and is configured to block{suffix}";
    }

    internal static string RunStatus(SelfClaw.Core.Runtime.Agent.RunCompletionStatus status)
        => status switch
        {
            SelfClaw.Core.Runtime.Agent.RunCompletionStatus.Succeeded => "succeeded",
            SelfClaw.Core.Runtime.Agent.RunCompletionStatus.Truncated => "truncated",
            SelfClaw.Core.Runtime.Agent.RunCompletionStatus.Blocked => "blocked",
            _ => "failed"
        };
}
