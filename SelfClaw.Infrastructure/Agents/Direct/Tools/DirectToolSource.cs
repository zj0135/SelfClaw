using System.Text.Json;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// Resolves the attribution a tool call carries. An MCP tool can name its server on the shared
/// descriptor because one binding belongs to one server, but the Skill tools are registered once for
/// every bound Skill, so their Skill id can only come from the call's own <c>skillId</c> argument.
/// Without it the transcript and the approval card lose the link back to the Skill that asked.
/// </summary>
internal static class DirectToolSource
{
    internal static string? ResolveId(DirectToolDescriptor descriptor, IDictionary<string, object?>? arguments)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.IsNullOrWhiteSpace(descriptor.SourceId))
        {
            return descriptor.SourceId;
        }

        if (descriptor.SourceKind != ToolSourceKind.Skill || arguments is null)
        {
            return null;
        }

        if (!arguments.TryGetValue("skillId", out var value))
        {
            return null;
        }

        // A provider connector hands arguments back as JSON-round-tripped values, so the skill id arrives
        // as a JsonElement rather than a string — the same shape DirectPromptComposer.ParseArguments
        // produces when it replays a stored call. Accepting only the CLR string silently dropped the
        // attribution for every real call.
        var skillId = value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(skillId) ? null : skillId;
    }
}
