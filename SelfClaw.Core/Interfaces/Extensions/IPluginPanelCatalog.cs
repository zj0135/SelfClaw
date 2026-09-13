using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IPluginPanelCatalog
{
    Task<IReadOnlyList<PluginPanelView>> ListPluginPanelViewsAsync(CancellationToken cancellationToken = default);
}
