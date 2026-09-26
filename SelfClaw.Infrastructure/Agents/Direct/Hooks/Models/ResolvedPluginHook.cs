using SelfClaw.Infrastructure.Extensions.Plugins.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record ResolvedPluginHook(
    string PluginId,
    string PluginVersion,
    string PluginRoot,
    PluginHookContribution Contribution,
    int DeclarationOrder,
    bool Inherited);
