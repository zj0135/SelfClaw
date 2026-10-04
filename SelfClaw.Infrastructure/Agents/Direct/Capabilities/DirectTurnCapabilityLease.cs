using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// One turn's resolved capabilities, plus the scope that owns every lease taken while resolving them - so
/// callers read the capabilities and release the leases through the same object. Idempotency belongs to the
/// scope; this type only holds it.
/// </summary>
internal sealed class DirectTurnCapabilityLease : IAsyncDisposable
{
    private readonly DirectTurnLeaseScope? _scope;

    public DirectTurnCapabilityLease(
        IReadOnlyList<string> systemInstructions,
        IReadOnlyList<DirectToolBinding> tools,
        IReadOnlyDictionary<Guid, string> messageAdjustments,
        IReadOnlyList<string> diagnostics,
        DirectTurnLeaseScope? scope = null,
        IReadOnlyList<ResolvedPluginHook>? hooks = null,
        IReadOnlyList<string>? hookNotices = null,
        string? hookBlockReason = null,
        IReadOnlyList<string>? explicitSkillIds = null)
    {
        SystemInstructions = systemInstructions;
        Tools = tools.Select(binding => (AITool)binding.Tool).ToArray();
        Bindings = tools.ToDictionary(binding => binding.Tool.Name, StringComparer.Ordinal);
        MessageAdjustments = messageAdjustments;
        Diagnostics = diagnostics;
        Hooks = hooks ?? [];
        HookNotices = hookNotices ?? [];
        HookBlockReason = hookBlockReason;
        ExplicitSkillIds = explicitSkillIds ?? [];
        _scope = scope;
    }

    public IReadOnlyList<string> SystemInstructions { get; }
    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyDictionary<string, DirectToolBinding> Bindings { get; }
    public IReadOnlyDictionary<Guid, string> MessageAdjustments { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<ResolvedPluginHook> Hooks { get; }
    public IReadOnlyList<string> HookNotices { get; }
    public string? HookBlockReason { get; }

    /// <summary>Skills the user explicitly activated for this turn; a steer cannot re-run that activation.</summary>
    public IReadOnlyList<string> ExplicitSkillIds { get; }

    public ValueTask DisposeAsync() => _scope?.DisposeAsync() ?? ValueTask.CompletedTask;
}
