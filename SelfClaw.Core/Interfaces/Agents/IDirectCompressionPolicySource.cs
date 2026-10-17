using SelfClaw.Core.Runtime;

namespace SelfClaw.Core.Interfaces;

public interface IDirectCompressionPolicySource
{
    Task<DirectCompressionPolicy> GetAsync(CancellationToken cancellationToken = default);
}
