using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IPluginViewCatalog
{
    Task<IReadOnlyList<PluginView>> ListPluginViewsAsync(CancellationToken cancellationToken = default);
}
