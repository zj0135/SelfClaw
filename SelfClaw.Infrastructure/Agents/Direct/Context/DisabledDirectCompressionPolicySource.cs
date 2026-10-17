using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// Default policy source used until a host supplies its settings-backed one: compaction stays off, so the
/// turn behaves exactly as it did before the feature existed and an oversized prompt still fails readably.
/// </summary>
internal sealed class DisabledDirectCompressionPolicySource : IDirectCompressionPolicySource
{
    private static readonly DirectCompressionPolicy DisabledPolicy = new(Enabled: false);

    internal static DisabledDirectCompressionPolicySource Instance { get; } = new();

    public Task<DirectCompressionPolicy> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DisabledPolicy);
}
