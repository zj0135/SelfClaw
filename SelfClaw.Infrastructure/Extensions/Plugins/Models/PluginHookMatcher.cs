using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record PluginHookMatcher(
    IReadOnlyList<DirectTurnOrigin> Origins,
    IReadOnlyList<string> ToolPatterns,
    IReadOnlyList<string> SourceIdPatterns,
    IReadOnlyList<ToolCallKind> Kinds,
    IReadOnlyList<ToolSourceKind> Sources,
    IReadOnlyList<string> HostPatterns);
